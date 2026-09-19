/* eslint-disable @typescript-eslint/no-explicit-any */
/* eslint-disable @typescript-eslint/no-unused-vars */
/* eslint-disable @typescript-eslint/no-unused-expressions */
import { useState, useRef, useEffect } from "react";
import { Document, Page, pdfjs } from "react-pdf";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import {
    DropdownMenu,
    DropdownMenuTrigger,
    DropdownMenuContent,
    DropdownMenuItem,
    DropdownMenuCheckboxItem,
} from "@/components/ui/dropdown-menu";
import { Save, Download, RefreshCw, Plus, Trash2, Pencil, Paperclip, History, Settings, Loader2, X, AlertTriangle, CheckCircle2 } from "lucide-react";
import { toast } from "sonner";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
    Select,
    SelectTrigger,
    SelectValue,
    SelectContent,
    SelectItem
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogFooter,
} from "@/components/ui/dialog";
import SignatureVerifyDialog from "./signature-verify-dialog";
import { baseCP, baseEID, baseEMAIL, baseFULLNAME, basePathUrl, baseUser_Type, authFetch, apiControllerBase } from "@/lib/config";

pdfjs.GlobalWorkerOptions.workerSrc = new URL(
    'pdfjs-dist/build/pdf.worker.min.mjs',
    import.meta.url,
).toString();


// Valid signatory_leveltype ids (id 2 is deliberately excluded — see
// ReferencesController.GetSignatoryLevels). Anything else — missing, 2, or
// garbage — falls back to 1 ("for signature", the plain default stamp).
const VALID_SIG_LEVELS = new Set([1, 3, 4]);
function normalizeSigLevel(level: number | null | undefined): string {
    return level != null && VALID_SIG_LEVELS.has(level) ? String(level) : "1";
}

type Signature = {
    id: string;
    pageNumber: number;
    x?: number;
    y?: number;
    xPct?: number;
    yPct?: number;
    isSpecimen: boolean;
    isDragging?: boolean;
};

// DTO returned by GetSignatoryBySigId API (camelCase — ASP.NET Core default JSON serialization)
type DocumentSignatoryDto = {
    sigId: number;
    docId: number;
    sigCode?: string | null;
    sigEid: number;
    sigStatus: number;
    sigOrder: number;
    sigRemarks?: string | null;
    sigUserType: number;
    sigLevel: number;
    sigSignCount: number;
    sigRemarksDatenTime?: string | null;
    dateTimeInserted?: string | null;
    signStatusId?: number;
    signStatusDescription?: string | null;
    fname?: string | null;
    position?: string | null;
    officeName?: string | null;
};


type PDFSigningViewProps = {
    doc_id: number;
    docDescription?: string;
    docStatus?: string;
    overlayWidth?: number;
    overlayHeight?: number;
    sig_id?: number;
    sigLevel?: number;
    sigSignCount?: number;
    preVerified?: { method: "pincode" | "password"; value: string } | null;
    onConsumePreVerified?: () => void;
    onSignedWithLocation?: (location: { page: number; xPct: number; yPct: number; isSpecimen: boolean; authorityLevel: number }) => void;
    viewOnly?: boolean;
    // When true, re-fetch the PDF after a successful save so the newly
    // embedded signature is visible. True for a single-document sign, and
    // for the last remaining unsigned document in a batch — in either case
    // there's nothing left to advance to. False mid-batch, since the view
    // moves on to the next document instead and reloading would be wasted.
    reloadAfterSave?: boolean;
    // Whether to show the "Show Info" / "Hide Info" toggle (and the
    // Document Info sidebar it opens) in the toolbar. Off for the
    // ForSignature batch-sign dialog, which has no room for it.
    showInfoToggle?: boolean;
    // When set, this signature is being applied by an alternate on behalf of
    // vwEids/vwUserType (the principal) instead of the logged-in user signing
    // for themselves. Undefined preserves today's behavior exactly.
    signAsAlternate?: { vwEids: string; vwUserType: string };
};

export default function PDFSigningView({ doc_id, docDescription, docStatus, sig_id, sigLevel, sigSignCount, preVerified, onConsumePreVerified, onSignedWithLocation, viewOnly = false, reloadAfterSave = false, showInfoToggle = true, signAsAlternate }: PDFSigningViewProps) {
    const [numPages, setNumPages] = useState<number>(0);
    const [pageWidth, setPageWidth] = useState<number>(0);
    const [pageHeight, setPageHeight] = useState<number>(0);
    const [basePageWidth, setBasePageWidth] = useState<number | null>(null);
    const [basePageHeight, setBasePageHeight] = useState<number | null>(null);
    const [signatures, setSignatures] = useState<Signature[]>([]);
    const [addMode, setAddMode] = useState<boolean>(true);

    const [signMultiple, setSignMultiple] = useState<boolean>(false);
    const [selectedPages, setSelectedPages] = useState<number[]>([]);
    const [zoom, setZoom] = useState<number>(1);
    const [dateDisplay, setDateDisplay] = useState<boolean>(false);
    // Gates the Authority Level override and "Hide Signature Date" controls —
    // membership in vip_no_date (by the signer's own eid/user_type), checked
    // once on mount. Everyone else just signs with the document's sig_level
    // and the date always shown.
    const [isVip, setIsVip] = useState<boolean>(false);
    // Whether this document's (doc_is, doc_type_id) combo is in
    // document_type_restricted — checked once per doc_id. When true,
    // non-VIP signers are hard-blocked from adding more signatures than
    // sig_sign_count (expectedSignCount); otherwise it's only ever a
    // warning. VIP signers are never blocked either way (see handlePageClick
    // and handleSave).
    const [isRestricted, setIsRestricted] = useState<boolean>(false);
    // Single source of truth for the stamp wording — seeded from the sigLevel
    // prop (available synchronously) and corroborated/corrected once
    // get_signatory_by_sigid resolves (see the fetchSignatory effect below).
    // No longer signer-editable: sig_level is set by the document uploader.
    const [authorityLevel, setAuthorityLevel] = useState<string>(normalizeSigLevel(sigLevel));
    // PDF fetch state
    const [pdfUrl, setPdfUrl] = useState<string | null>(null);
    const [pdfLoading, setPdfLoading] = useState<boolean>(true);
    // True for the whole duration of any PDF fetch, including the quiet
    // post-save reload — drives the floating loader so that reload isn't
    // silent, without triggering the full-page skeleton (`pdfLoading`).
    const [pdfFetching, setPdfFetching] = useState<boolean>(false);
    const [pdfError, setPdfError] = useState<string | null>(null);
    const [pdfRetry, setPdfRetry] = useState(0);
    const hasLoadedPdfOnceRef = useRef(false);
    // When a post-save reload is in flight, keep showing the local signature
    // overlay (it already looks like the real thing) until the re-fetched
    // PDF actually lands — avoids a blank/unsigned flash during the fetch.
    const clearSignaturesOnNextLoadRef = useRef(false);
    const containerRef = useRef<HTMLDivElement>(null);
    // Sidebar toggle state
    const [sidebarOpen, setSidebarOpen] = useState<boolean>(false);

    // Refs to track which page is being dragged
    const draggedSignaturePageRef = useRef<number | null>(null);
    // Immediate refs for dragged IDs to use inside event listeners
    const draggedSignatureIdRef = useRef<string | null>(null);
    const draggedElementRef = useRef<HTMLDivElement | null>(null);
    const dragOffsetRef = useRef<{ x: number; y: number }>({ x: 0, y: 0 });
    const [signatureImageUrl, setSignatureImageUrl] = useState<string | null>(null)
    const [loadingSignature, setLoadingSignature] = useState(true)
    const [errorSignature, setErrorSignature] = useState(false)

    // Save state
    const [saving, setSaving] = useState<boolean>(false);
    // Verification dialog state
    const [verifyDialogOpen, setVerifyDialogOpen] = useState<boolean>(false);
    const [hasPfxPassword, setHasPfxPassword] = useState<boolean>(false);
    const [checkingPfx, setCheckingPfx] = useState<boolean>(false);
    // Signature-count mismatch confirm dialog (non-VIP only — see handleSave).
    // Resolves the pending confirmMismatch() promise; always closed (and
    // resolved false) before the verify dialog above ever opens, so the two
    // never show at once.
    const [mismatchDialog, setMismatchDialog] = useState<{ message: string; resolve: (ok: boolean) => void } | null>(null);
    const confirmMismatch = (message: string): Promise<boolean> =>
        new Promise((resolve) => setMismatchDialog({ message, resolve }));

    // Signatory info fetched from API (by sig_id prop)
    const [signatory, setSignatory] = useState<DocumentSignatoryDto | null>(null);
    const [expectedSignCount, setExpectedSignCount] = useState<number>(3);
    const [, setSignatoryLoading] = useState<boolean>(false);
    const [, setSignatoryError] = useState<string | null>(null);

    // Fetch signature image
    useEffect(() => {
        let revoke: string | null = null;
        const fetchSignature = async () => {
            try {
                setLoadingSignature(true);
                const url = `${basePathUrl}api/${apiControllerBase}/get_signature_image_merged?eids=${baseEID}&usertypes=${baseUser_Type}&type=signature`;
                const response = await authFetch(url);
                if (!response.ok) throw new Error('Failed to fetch signature');
                const blob = await response.blob();
                const objectUrl = URL.createObjectURL(blob);
                revoke = objectUrl;
                setSignatureImageUrl(objectUrl);
            } catch (err) {
                console.error('Error fetching signature:', err);
                setErrorSignature(true);
            } finally {
                setLoadingSignature(false);
            }
        };

        fetchSignature();

        return () => {
            if (revoke) URL.revokeObjectURL(revoke);
        };
    }, []);

    // VVIP check — gates the Authority Level override and "Hide Signature
    // Date" controls (see isVip declaration above).
    useEffect(() => {
        const controller = new AbortController();
        const checkVip = async () => {
            try {
                const url = `${basePathUrl}api/References/check_vip?eid=${baseEID}&userType=${baseUser_Type}`;
                const res = await authFetch(url, { signal: controller.signal });
                if (!res.ok) return;
                const data = await res.json();
                setIsVip(!!data.isVip);
            } catch {
                // Non-critical — defaults to non-VIP (the safe default) on failure.
            }
        };
        checkVip();
        return () => controller.abort();
    }, []);

    // Restricted-document-type check — gates whether exceeding the assigned
    // signature count is a hard block or just a warning (see isRestricted
    // declaration above).
    useEffect(() => {
        const controller = new AbortController();
        const checkRestriction = async () => {
            try {
                const url = `${basePathUrl}api/References/get_signature_restriction?docId=${doc_id}`;
                const res = await authFetch(url, { signal: controller.signal });
                if (!res.ok) return;
                const data = await res.json();
                setIsRestricted(!!data.isRestricted);
            } catch {
                // Non-critical — defaults to unrestricted (warning-only) on failure.
            }
        };
        checkRestriction();
        return () => controller.abort();
    }, [doc_id]);


    // Fetch signatory by sig_id (if provided) and map fields to UI state
    useEffect(() => {
        console.log('[fetchSignatory] sig_id =', sig_id);
        if (sig_id == null) return;
        const controller = new AbortController();

        const fetchSignatory = async () => {
            setSignatoryLoading(true);
            setSignatoryError(null);
            const url = `${basePathUrl}api/${apiControllerBase}/get_signatory_by_sigid?sigId=${sig_id}`;
            console.log('[fetchSignatory] url =', url);
            try {
                const res = await authFetch(url, {
                    method: 'GET',
                    headers: { 'Content-Type': 'application/json' },
                    signal: controller.signal,
                });
                console.log('[fetchSignatory] response:', res.status, res.ok, res.url);
                if (!res.ok) throw new Error(`Failed to fetch signatory (${res.status})`);
                const data: DocumentSignatoryDto = await res.json();
                console.log('[fetchSignatory] data:', data);
                setSignatory(data);
                // Map sigLevel -> authorityLevel (string), normalized to a
                // valid level (see normalizeSigLevel above).
                if (typeof data.sigLevel === 'number') setAuthorityLevel(normalizeSigLevel(data.sigLevel));

                // Use sigSignCount as expected sign count in UI
                if (typeof data.sigSignCount === 'number') setExpectedSignCount(data.sigSignCount);
                console.log(signatory)
            } catch (err: unknown) {
                if (err instanceof Error) setSignatoryError(err.message);
                else setSignatoryError('Unknown error');
            } finally {
                -
                    setSignatoryLoading(false);
            }
        };

        fetchSignatory();
        return () => controller.abort();
    }, [sig_id]);



    // Fetch PDF only
    useEffect(() => {
        let revoke: string | null = null;
        const controller = new AbortController();
        // Only the very first load shows the skeleton/spinner. Reloads triggered
        // after saving a signature (via pdfRetry) fetch quietly in the background
        // and just swap the PDF in once ready — no loading flash.
        const isFirstLoad = !hasLoadedPdfOnceRef.current;

        const fetchPdf = async () => {
            if (isFirstLoad) setPdfLoading(true);
            setPdfFetching(true);
            setPdfError(null);
            try {
                const res = await authFetch(
                    `${basePathUrl}api/${apiControllerBase}/get_pdf_digital_only?formId=${doc_id}&isDownload=0`,
                    { signal: controller.signal }
                );
                console.log("Fetch PDF response:", res);
                console.log("Fetch PDF doc_id:", doc_id);
                console.log("Fetch PDF sig_id:", sig_id);
                if (!res.ok) throw new Error(`Failed to load PDF (${res.status})`);
                const blob = await res.blob();
                const url = URL.createObjectURL(blob);
                console.log('Blob size:', blob.size);
                revoke = url;
                setPdfUrl(url);
                hasLoadedPdfOnceRef.current = true;
                if (clearSignaturesOnNextLoadRef.current) {
                    clearSignaturesOnNextLoadRef.current = false;
                    setSignatures([]);
                }
            } catch (err: unknown) {
                if (err instanceof Error && err.name !== "AbortError") {
                    setPdfError(err.message);
                }
            } finally {
                if (isFirstLoad) setPdfLoading(false);
                setPdfFetching(false);
            }
        };

        fetchPdf();

        return () => {
            controller.abort();
            if (revoke) URL.revokeObjectURL(revoke);
        };
    }, [doc_id, pdfRetry]);

    // Handle document load
    const onDocumentLoadSuccess = ({ numPages }: { numPages: number }) => {
        setNumPages(numPages);
        setSelectedPages(Array.from({ length: numPages }, (_, i) => i + 1));
    };

    // Handle page click to add signature or stamp
    const handlePageClick = (
        event: React.MouseEvent<HTMLDivElement>,
        pageNumber: number
    ) => {

        const rect = event.currentTarget.getBoundingClientRect();
        const offsetX = event.clientX - rect.left - 40; // Center for stamp image (80x80)
        const offsetY = event.clientY - rect.top - 40;

        if (addMode) {
            const pagesToAdd = signMultiple ? selectedPages : [pageNumber];
            const wouldExceed = signatures.length + pagesToAdd.length > expectedSignCount;

            if (wouldExceed) {
                if (isRestricted && !isVip) {
                    // Strictly enforced for this document type — block outright.
                    toast.error(
                        `This document type only allows ${expectedSignCount} signature${expectedSignCount === 1 ? '' : 's'}. You can't add more.`
                    );
                    return;
                }
                // Not restricted, or a VVIP signer — allow it, just warn.
                toast.warning(
                    `You're adding more signatures (${signatures.length + pagesToAdd.length}) than the ${expectedSignCount} assigned to you.`
                );
            }

            const newSignatures = pagesToAdd.map((pg) => ({
                id: `${pg}-${Date.now()}-${Math.random()}`,
                pageNumber: pg,
                xPct: Math.max(0, offsetX) / rect.width,
                yPct: Math.max(0, offsetY) / rect.height,
                isSpecimen: false,
            }));
            setSignatures([...signatures, ...newSignatures]);
            setAddMode(false);
        }
    };

    // ===================== Signature Drag Handlers =====================
    const handleSignatureMouseDown = (e: React.MouseEvent | React.TouchEvent, sigId: string) => {
        e.stopPropagation();
        let startX: number, startY: number;
        if ('touches' in e) {
            (e as React.TouchEvent).preventDefault();
            startX = e.touches[0].clientX;
            startY = e.touches[0].clientY;
        } else {
            startX = e.clientX;
            startY = e.clientY;
        }

        const sig = signatures.find(s => s.id === sigId);
        if (!sig) return;

        const pageDiv = document.getElementById(`page-${sig.pageNumber}`);
        if (!pageDiv) return;
        const rect = pageDiv.getBoundingClientRect();
        const mousePageX = startX - rect.left;
        const mousePageY = startY - rect.top;

        // compute signature current pixel position from percentage coords
        const sigX = (sig.xPct ?? 0) * rect.width;
        const sigY = (sig.yPct ?? 0) * rect.height;

        // Store offset in ref (avoids state updates during drag)
        dragOffsetRef.current = { x: mousePageX - sigX, y: mousePageY - sigY };
        draggedSignatureIdRef.current = sigId;
        draggedSignaturePageRef.current = sig.pageNumber;

        // Grab DOM element for direct manipulation during drag
        const element = e.currentTarget as HTMLDivElement;
        draggedElementRef.current = element;
        element.style.willChange = 'left, top';

        setSignatures(prev =>
            prev.map(s => s.id === sigId ? { ...s, isDragging: true } : s)
        );

        if ('touches' in e) {
            window.addEventListener('touchmove', handleSignatureTouchMove, { passive: false });
            window.addEventListener('touchend', handleSignatureTouchEnd, { passive: false });
        } else {
            window.addEventListener('mousemove', handleSignatureMouseMove);
            window.addEventListener('mouseup', handleSignatureMouseUp);
        }
    };

    // Direct DOM update – bypasses React for smooth 60fps dragging
    const handleSignatureMouseMove = (e: MouseEvent) => {
        const pageNumber = draggedSignaturePageRef.current;
        if (pageNumber === null || !draggedElementRef.current) return;

        const pageDiv = document.getElementById(`page-${pageNumber}`);
        if (!pageDiv) return;
        const rect = pageDiv.getBoundingClientRect();

        const newX = (e.clientX - rect.left) - dragOffsetRef.current.x;
        const newY = (e.clientY - rect.top) - dragOffsetRef.current.y;
        const clampedX = Math.max(0, Math.min(rect.width, newX));
        const clampedY = Math.max(0, Math.min(rect.height, newY));

        draggedElementRef.current.style.left = `${clampedX}px`;
        draggedElementRef.current.style.top = `${clampedY}px`;
    };

    // Commit final position to React state on release
    const handleSignatureMouseUp = () => {
        const element = draggedElementRef.current;
        const pageNumber = draggedSignaturePageRef.current;
        const sigId = draggedSignatureIdRef.current;

        if (element && pageNumber !== null && sigId) {
            element.style.willChange = '';
            const pageDiv = document.getElementById(`page-${pageNumber}`);
            if (pageDiv) {
                const rect = pageDiv.getBoundingClientRect();
                const finalLeft = parseFloat(element.style.left) || 0;
                const finalTop = parseFloat(element.style.top) || 0;
                const newXPct = Math.max(0, Math.min(1, finalLeft / rect.width));
                const newYPct = Math.max(0, Math.min(1, finalTop / rect.height));
                // While "Sign Multiple Pages" is on, every placed signature is
                // one linked group sharing a single x/y — moving any one of
                // them moves them all, uniformly, to the same spot on their
                // own page. Turning the switch off later leaves them exactly
                // where they are (nothing is deleted) and each becomes
                // independently draggable again, since this branch simply
                // stops running.
                setSignatures(prev =>
                    signMultiple
                        ? prev.map(s => ({ ...s, xPct: newXPct, yPct: newYPct, isDragging: false }))
                        : prev.map(s => s.id === sigId ? { ...s, xPct: newXPct, yPct: newYPct, isDragging: false } : s)
                );
            } else {
                setSignatures(prev =>
                    prev.map(s => s.isDragging ? { ...s, isDragging: false } : s)
                );
            }
        }

        draggedElementRef.current = null;
        draggedSignatureIdRef.current = null;
        draggedSignaturePageRef.current = null;
        window.removeEventListener('mousemove', handleSignatureMouseMove);
        window.removeEventListener('mouseup', handleSignatureMouseUp);
    };

    const handleSignatureTouchMove = (e: TouchEvent) => {
        const pageNumber = draggedSignaturePageRef.current;
        if (pageNumber === null || !draggedElementRef.current) return;

        e.preventDefault();

        const pageDiv = document.getElementById(`page-${pageNumber}`);
        if (!pageDiv) return;
        const rect = pageDiv.getBoundingClientRect();

        const newX = (e.touches[0].clientX - rect.left) - dragOffsetRef.current.x;
        const newY = (e.touches[0].clientY - rect.top) - dragOffsetRef.current.y;
        const clampedX = Math.max(0, Math.min(rect.width, newX));
        const clampedY = Math.max(0, Math.min(rect.height, newY));

        draggedElementRef.current.style.left = `${clampedX}px`;
        draggedElementRef.current.style.top = `${clampedY}px`;
    };

    const handleSignatureTouchEnd = () => {
        const element = draggedElementRef.current;
        const pageNumber = draggedSignaturePageRef.current;
        const sigId = draggedSignatureIdRef.current;

        if (element && pageNumber !== null && sigId) {
            element.style.willChange = '';
            const pageDiv = document.getElementById(`page-${pageNumber}`);
            if (pageDiv) {
                const rect = pageDiv.getBoundingClientRect();
                const finalLeft = parseFloat(element.style.left) || 0;
                const finalTop = parseFloat(element.style.top) || 0;
                const newXPct = Math.max(0, Math.min(1, finalLeft / rect.width));
                const newYPct = Math.max(0, Math.min(1, finalTop / rect.height));
                // Same linked-group rule as handleSignatureMouseUp — see the
                // comment there.
                setSignatures(prev =>
                    signMultiple
                        ? prev.map(s => ({ ...s, xPct: newXPct, yPct: newYPct, isDragging: false }))
                        : prev.map(s => s.id === sigId ? { ...s, xPct: newXPct, yPct: newYPct, isDragging: false } : s)
                );
            } else {
                setSignatures(prev =>
                    prev.map(s => s.isDragging ? { ...s, isDragging: false } : s)
                );
            }
        }

        draggedElementRef.current = null;
        draggedSignatureIdRef.current = null;
        draggedSignaturePageRef.current = null;
        window.removeEventListener('touchmove', handleSignatureTouchMove, { passive: false } as EventListenerOptions);
        window.removeEventListener('touchend', handleSignatureTouchEnd, { passive: false } as EventListenerOptions);
    };




    // ===================== Save Signature Handler =====================
    const handleSave = async () => {
        if (signatures.length === 0) {
            toast.warning("No signatures placed. Please add at least one signature before saving.");
            return;
        }

        // Exceed/lacking checks vs. the assigned sig_sign_count. VVIP signers
        // (the hard "restricted" block only ever applies to non-VIP signers
        // when adding, see handlePageClick) just get a warning popup and
        // proceed. Everyone else gets the same warning plus an explicit
        // confirm — saving with a mismatched count is deliberate, not a
        // silent default.
        if (signatures.length !== expectedSignCount) {
            const mismatchMessage = signatures.length > expectedSignCount
                ? `You're saving ${signatures.length} signatures — more than the ${expectedSignCount} assigned to you.`
                : `You're saving ${signatures.length} signature${signatures.length === 1 ? '' : 's'} — fewer than the ${expectedSignCount} assigned to you.`;

            toast.warning(mismatchMessage);

            if (!isVip && !(await confirmMismatch(mismatchMessage))) {
                return;
            }
        }

        // If parent provided pre-verified credentials via `preVerified` prop, use them directly and skip dialog
        if (preVerified && (preVerified.method === 'pincode' || preVerified.method === 'password')) {
            try {
                let ok = false;
                if (preVerified.method === 'pincode') {
                    ok = await performSaveAction('', preVerified.value);
                } else {
                    ok = await performSaveAction(preVerified.value, '');
                }
                if (ok) {
                    onConsumePreVerified && onConsumePreVerified();
                }
            } catch (err) {
                // errors handled inside performSaveAction
            }
            return;
        }
        setCheckingPfx(true);
        try {
            const pfxRes = await authFetch(
                `${basePathUrl}api/${apiControllerBase}/get_pfx_attachments_by_eid?eid=${baseEID}&userType=${baseUser_Type}`
            );
            if (!pfxRes.ok) throw new Error("Failed to check certificate status");
            const hasPassword: boolean = await pfxRes.json();
            setHasPfxPassword(hasPassword);
            setVerifyDialogOpen(true);
        } catch (err) {
            console.error("PFX check error:", err);
            toast.error("Failed to verify certificate status.");
        } finally {
            setCheckingPfx(false);
        }
    };

    // Step 2: Called when user submits verification from the dialog
    //
    // Flow:
    //   hasPfxPassword=true  → user picks PIN/SMS/email → verify locally → send empty bulkPasswords
    //                          (controller decrypts the saved password from DB)
    //   hasPfxPassword=false → user types the real PFX password → send it as bulkPasswords
    const handleVerifyAndSign = async (
        data: { method: "sms" | "email" | "pincode"; pin: string } | { method: "password"; password: string }
    ): Promise<boolean> => {
        // Only use the password the user typed when there is NO saved password in the DB.
        // For PIN/SMS/email methods the controller resolves the real PFX password from the DB.
        const password = data.method === "password" ? data.password : "";

        setSaving(true);
        try {
            // ── Pin Code: verify against get_pincode_by_eid first ──
            if (data.method === "pincode") {
                const pinRes = await authFetch(
                    `${basePathUrl}api/${apiControllerBase}/get_pincode_by_eid?eid=${baseEID}&userType=${baseUser_Type}&pincode=${encodeURIComponent(data.pin)}`
                );
                if (!pinRes.ok) throw new Error("Failed to verify pin code");
                const pinValid: boolean = await pinRes.json();
                if (!pinValid) {
                    toast.error("Invalid pin code. Please try again.");
                    setSaving(false);
                    return false;
                }
            }

            // ── Password entered directly: verify via checkpassword API before attempting save ──
            if (data.method === "password") {
                try {
                    const checkRes = await authFetch(
                        `${basePathUrl}api/${apiControllerBase}/get_checkpassword_by_eid?eid=${baseEID}&userType=${baseUser_Type}&password=${encodeURIComponent(
                            (data as { method: "password"; password: string }).password
                        )}`
                    );
                    if (!checkRes.ok) throw new Error("Failed to verify password");
                    const passOk: boolean = await checkRes.json();
                    if (!passOk) {
                        toast.error("Invalid certificate password. Please try again.");
                        setSaving(false);
                        return false;
                    }
                } catch (err) {
                    console.error("Password check error:", err);
                    toast.error("Failed to verify certificate password.");
                    setSaving(false);
                    return false;
                }
            }

            // If we reached here, verification passed — delegate to shared save routine
            const pinValue = (data as any).pin ? (data as any).pin : "";
            const ok = await performSaveAction(password, pinValue);
            return ok;
        } catch (err) {
            console.error("Save signature error:", err);
            toast.error("Something went wrong while saving the signature.");
            return false;
        } finally {
            setSaving(false);
        }
    };

    // Shared save routine: builds payload and calls save API. Returns boolean.
    const performSaveAction = async (password: string, pin: string): Promise<boolean> => {
        setSaving(true);
        try {
            setVerifyDialogOpen(false);

            const signatureLocations = signatures.map((sig) => ({
                page: sig.pageNumber,
                xPct: sig.xPct ?? 0,
                yPct: sig.yPct ?? 0,
                specimenType: sig.isSpecimen ? 1 : 0,
                authorityLevel: Number(authorityLevel),
            }));

            // Try to get geolocation
            let latitude = "";
            let longitude = "";
            let accuracy = "";
            try {
                const pos = await new Promise<GeolocationPosition>((resolve, reject) =>
                    navigator.geolocation.getCurrentPosition(resolve, reject, { timeout: 5000 })
                );
                latitude = String(pos.coords.latitude);
                longitude = String(pos.coords.longitude);
                accuracy = String(pos.coords.accuracy);
            } catch {
                // ignore
            }

            const deviceType = /Mobi|Android/i.test(navigator.userAgent) ? "Mobile" : "Desktop";

            const payload = {
                docId: String(doc_id),
                eid: String(baseEID),
                userType: String(baseUser_Type),
                signatures: signatureLocations,
                bulkPasswords: password,
                PinCode: pin || "",
                bulkDeviceType: deviceType,
                modsId: "1",
                isDisplayDate: dateDisplay ? 1 : 0,
                isDelegate: 0,
                isAlternate: signAsAlternate ? 1 : 0,
                vwEids: signAsAlternate?.vwEids ?? "",
                vwUserType: signAsAlternate?.vwUserType ?? "",
                dateNTimeClick: new Date().toISOString(),
                dgLatitude: latitude,
                dgLongitude: longitude,
                dgAccuracy: accuracy,
                dgDeviceType: deviceType,
                dgAddress: "",
            };

            console.log("save_signature_image payload:", payload);

            const res = await authFetch(`${basePathUrl}api/${apiControllerBase}/save_signature_image`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(payload),
            });

            const resData = await res.json();
            console.log("save_signature_image response:", resData);
            // TEMP DEBUG: server sends the real exception text here (Development only,
            // see SigningService.SaveSignatureImageAsync's catch block) — surface it
            // loudly instead of leaving it buried inside the logged object above.
            if (resData?.debugError) {
                console.error("save_signature_image server error:", resData.debugError);
            }

            if (!res.ok || !resData.success) {
                toast.error(resData.message || "Failed to save signature.");
                return false;
            }

            toast.success("Signature saved successfully!");
            if (onSignedWithLocation && signatureLocations.length > 0) {
                onSignedWithLocation({
                    page: signatureLocations[0].page,
                    xPct: signatureLocations[0].xPct,
                    yPct: signatureLocations[0].yPct,
                    isSpecimen: signatureLocations[0].specimenType === 1,
                    authorityLevel: signatureLocations[0].authorityLevel,
                });
            }
            if (reloadAfterSave) {
                // Leave the local overlay in place — it already shows the
                // signature at the right spot — and swap it for the real
                // embedded PDF once the re-fetch below actually lands.
                clearSignaturesOnNextLoadRef.current = true;
                setPdfRetry((c) => c + 1);
            } else {
                setSignatures([]);
            }
            return true;
        } catch (err) {
            console.error("Save signature error:", err);
            toast.error("Something went wrong while saving the signature.");
            return false;
        } finally {
            setSaving(false);
        }
    };

    // Toggle specimen
    const toggleSpecimen = (id: string) => {
        setSignatures((prev) =>
            prev.map((sig) =>
                sig.id === id ? { ...sig, isSpecimen: !sig.isSpecimen } : sig
            )
        );
    };

    // Delete signature
    const deleteSignature = (id: string) => {
        setSignatures((prev) => prev.filter((sig) => sig.id !== id));
    };

    // Page size callback (to get page dimensions)
    const onPageLoadSuccess = (page: { getViewport: (options: { scale: number }) => { width: number; height: number } }) => {
        // get natural size at scale=1 so we can compute responsive scaling
        const baseViewport = page.getViewport({ scale: 1 });
        setBasePageWidth(baseViewport.width);
        setBasePageHeight(baseViewport.height);

        // compute desired width to fit container (mobile) or use zoom-based scale
        const container = containerRef.current;
        const containerWidth = container ? container.getBoundingClientRect().width : baseViewport.width;
        // prefer fitting to container for small screens
        const fittedWidth = Math.min(baseViewport.width * zoom, containerWidth - 16);
        const scale = fittedWidth / baseViewport.width;
        const width = baseViewport.width * scale;
        const height = baseViewport.height * scale;
        setPageWidth(width);
        setPageHeight(height);
    };

    // Recompute page size on resize or zoom change
    useEffect(() => {
        const recompute = () => {
            if (!basePageWidth || !basePageHeight) return;
            const container = containerRef.current;
            const containerWidth = container ? container.getBoundingClientRect().width : basePageWidth;
            const fittedWidth = Math.min(basePageWidth * zoom, containerWidth - 16);
            const scale = fittedWidth / basePageWidth;
            setPageWidth(basePageWidth * scale);
            setPageHeight(basePageHeight * scale);
        };
        recompute();
        window.addEventListener('resize', recompute);
        return () => window.removeEventListener('resize', recompute);
    }, [basePageWidth, basePageHeight, zoom]);

    // Toggle all pages for multi‑sign
    const toggleAllPages = (checked: boolean) => {
        setSelectedPages(checked ? Array.from({ length: numPages }, (_, i) => i + 1) : []);
    };

    // Scale factor so signature overlays grow/shrink with zoom
    const effectiveScale = basePageWidth ? pageWidth / basePageWidth : 1;

    return (
        <div className="flex flex-col h-screen bg-background">
            {/* Floating centered loader — shown while saving, on the initial PDF load, and
                during the quiet post-save reload (pdfFetching) so that wait isn't silent */}
            {(saving || pdfFetching) && (
                <div className="fixed inset-0 z-1000 flex items-center justify-center bg-background/60 backdrop-blur-sm">
                    <div className="flex flex-col items-center gap-3 bg-card border shadow-2xl rounded-2xl px-8 py-6">
                        <Loader2 className="h-8 w-8 animate-spin text-primary" />
                        <p className="text-sm font-medium text-foreground whitespace-nowrap">
                            {saving ? 'Saving signature…' : pdfLoading ? 'Loading PDF…' : 'Applying signature…'}
                        </p>
                    </div>
                </div>
            )}
            {/* DEBUG BANNER — remove after testing */}
            {/* <div className="bg-yellow-300 text-black text-xs p-1 text-center font-mono">
        DEBUG: sig_id={String(sig_id)} | doc_id={String(doc_id)} | signatoryLoading={String(signatoryLoading)} | signatoryError={signatoryError ?? 'none'}
      </div> */}
            {/* Toolbar */}
            <div className="sticky top-0 z-1 bg-background border-b shadow-sm px-2 py-2 flex items-center gap-1">
                {viewOnly ? (
                    <span className="text-[11px] bg-green-600 text-white px-2 py-0.5 rounded-full mr-1 whitespace-nowrap flex items-center gap-1">
                        <CheckCircle2 className="h-3 w-3" /> Signed
                    </span>
                ) : (
                    <>
                        {addMode && (
                            <span className="text-[11px] bg-primary text-primary-foreground px-2 py-0.5 rounded-full animate-pulse mr-1 whitespace-nowrap">
                                Tap page to place
                            </span>
                        )}
                        <Button variant="ghost" size="icon" onClick={() => setAddMode(true)} title="Add signature">
                            <Plus className="h-5 w-5" />
                        </Button>
                        <Button variant="ghost" size="icon" onClick={handleSave} disabled={saving || checkingPfx} title="Save">
                            {saving || checkingPfx ? <Loader2 className="h-5 w-5 animate-spin" /> : <Save className="h-5 w-5" />}
                        </Button>
                    </>
                )}
                <Button variant="ghost" size="icon" className="hidden sm:inline-flex" title="Download">
                    <Download className="h-5 w-5" />
                </Button>

                <div className="flex-1" />

                {/* Zoom controls */}
                <Button variant="outline" size="sm" className="h-7 px-2 text-base leading-none" onClick={() => setZoom((z) => Math.max(0.5, z - 0.25))}>−</Button>
                <span className="text-xs font-bold px-1.5 min-w-[3.5ch] text-center tabular-nums">{Math.round(zoom * 100)}%</span>
                <Button variant="outline" size="sm" className="h-7 px-2 text-base leading-none" onClick={() => setZoom((z) => Math.min(3, z + 0.25))}>+</Button>

                {/* Desktop: inline toggle */}
                {showInfoToggle && (
                    <Button variant="outline" size="sm" className="hidden sm:inline-flex ml-1" onClick={() => setSidebarOpen((v) => !v)}>
                        {sidebarOpen ? 'Hide Info' : 'Show Info'}
                    </Button>
                )}

                {/* Mobile: overflow menu */}
                <DropdownMenu>
                    <DropdownMenuTrigger className="sm:hidden ml-1 inline-flex h-9 w-9 items-center justify-center rounded-md hover:bg-accent hover:text-accent-foreground">
                        <Settings className="h-4 w-4" />
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end">
                        <DropdownMenuItem onClick={() => setPdfRetry(c => c + 1)}>
                            <RefreshCw className="h-4 w-4 mr-2" /> Reload PDF
                        </DropdownMenuItem>
                        <DropdownMenuItem>
                            <Download className="h-4 w-4 mr-2" /> Download
                        </DropdownMenuItem>
                        {showInfoToggle && (
                            <DropdownMenuItem onClick={() => setSidebarOpen(v => !v)}>
                                <History className="h-4 w-4 mr-2" /> {sidebarOpen ? 'Hide Info' : 'Show Info'}
                            </DropdownMenuItem>
                        )}
                    </DropdownMenuContent>
                </DropdownMenu>
            </div>

            {/* Signed banner */}
            {viewOnly && (
                <div className="bg-green-50 dark:bg-green-900/20 border-b border-green-200 dark:border-green-700 px-4 py-2.5 flex items-center gap-2 text-green-800 dark:text-green-300 text-sm font-medium">
                    <CheckCircle2 className="h-4 w-4 shrink-0" />
                    Document signed — viewing in read-only mode.
                </div>
            )}

            {/* Main area */}
            <div className="flex flex-1 overflow-hidden rounded-xl shadow-lg mt-2">
                {/* PDF container */}
                <ScrollArea className="flex-1 relative bg-muted/50 p-4">
                    <div ref={containerRef}>
                        {pdfLoading && (
                            <div className="space-y-4 p-2">
                                <div className="flex items-center gap-2 pb-1">
                                    <Loader2 className="h-4 w-4 animate-spin text-primary shrink-0" />
                                    <span className="text-sm text-muted-foreground">Loading PDF…</span>
                                </div>
                                {Array.from({ length: 3 }).map((_, i) => (
                                    <div
                                        key={i}
                                        className="relative rounded-lg overflow-hidden bg-card border shadow mx-auto"
                                        style={{ width: '100%', aspectRatio: '0.707' }}
                                    >
                                        <div className="absolute inset-0 animate-pulse bg-muted" />
                                        <div className="absolute inset-0 p-6 pt-8 space-y-2.5 pointer-events-none">
                                            <div className="h-3.5 bg-muted-foreground/20 rounded animate-pulse w-1/2 mx-auto" style={{ animationDelay: '0ms' }} />
                                            <div className="h-px bg-muted-foreground/10 my-3" />
                                            {Array.from({ length: 8 }).map((_, j) => (
                                                <div
                                                    key={j}
                                                    className="h-2.5 bg-muted-foreground/15 rounded animate-pulse"
                                                    style={{ width: `${70 + Math.sin(i * 3 + j) * 22}%`, animationDelay: `${j * 60}ms` }}
                                                />
                                            ))}
                                            <div className="h-4" />
                                            {Array.from({ length: 5 }).map((_, j) => (
                                                <div
                                                    key={`b${j}`}
                                                    className="h-2.5 bg-muted-foreground/15 rounded animate-pulse"
                                                    style={{ width: `${60 + Math.cos(i * 2 + j) * 28}%`, animationDelay: `${(j + 9) * 60}ms` }}
                                                />
                                            ))}
                                        </div>
                                        <div className="absolute bottom-2 right-3 text-[10px] text-muted-foreground/30 font-mono select-none">
                                            {i + 1}
                                        </div>
                                    </div>
                                ))}
                            </div>
                        )}
                        {pdfError && (
                            <div className="flex flex-col items-center gap-3 py-16 px-4 text-center">
                                <div className="p-4 rounded-full bg-destructive/10">
                                    <AlertTriangle className="h-8 w-8 text-destructive" />
                                </div>
                                <div>
                                    <p className="font-semibold text-destructive">Failed to load PDF</p>
                                    <p className="text-sm text-muted-foreground mt-1">{pdfError}</p>
                                </div>
                                <Button variant="outline" size="sm" className="mt-2" onClick={() => setPdfRetry(c => c + 1)}>
                                    <RefreshCw className="h-4 w-4 mr-2" /> Retry
                                </Button>
                            </div>
                        )}
                        {pdfUrl && (
                            <Document
                                file={pdfUrl}
                                onLoadSuccess={onDocumentLoadSuccess}
                                loading={<div className="flex items-center justify-center gap-2 p-6 text-muted-foreground"><Loader2 className="h-5 w-5 animate-spin" /><span className="text-sm">Rendering pages…</span></div>}
                            >
                                {Array.from({ length: numPages }, (_, index) => index + 1).map((pageNumber) => (
                                    <div
                                        key={pageNumber}
                                        id={`page-${pageNumber}`}
                                        className="relative mb-4 mx-auto shadow-xl bg-card rounded-lg border"
                                        style={{ width: pageWidth, height: pageHeight }}
                                        onClick={viewOnly ? undefined : (e) => handlePageClick(e, pageNumber)}
                                    >
                                        <Page
                                            pageNumber={pageNumber}
                                            scale={zoom}
                                            onLoadSuccess={onPageLoadSuccess}
                                            renderTextLayer={false}
                                            renderAnnotationLayer={false}
                                        />
                                        {/* Signature overlays for this page */}
                                        {signatures
                                            .filter((sig) => sig.pageNumber === pageNumber)
                                            .map((sig) => {
                                                const left = (sig.xPct ?? 0) * pageWidth;
                                                const top = (sig.yPct ?? 0) * pageHeight;

                                                // ====== CONFIGURABLE SIZES — must match Spire PDF signature bounds: 190×50 pts ======
                                                const holderWidth = 190;   // matches PDF signature width (190 pts)
                                                const holderHeight = 70;   // 50px image area + 20px drag controls
                                                const imgWidth = 190;      // signature image width (px)
                                                const imgHeight = 50;      // matches PDF signature height (50 pts)
                                                // Text starts at right half (left:95 = midpoint of 190px — mirrors Spire SignImageAndSignDetail layout)
                                                const textTop = 5;          // px from top edge of image area
                                                const textFontSize = 5.5;   // px font size for text
                                                // ==========================================================

                                                return (
                                                    <div
                                                        key={sig.id}
                                                        className="absolute rounded-md select-none"
                                                        style={{
                                                            left,
                                                            top,
                                                            zIndex: sig.isDragging ? 50 : 10,
                                                            width: holderWidth,
                                                            height: holderHeight,
                                                            transform: `scale(${effectiveScale})`,
                                                            transformOrigin: 'top left',
                                                        }}
                                                        onMouseDown={(e: any) => {
                                                            const tgt = e.target as HTMLElement | null;
                                                            if (tgt && tgt.closest && tgt.closest('button,input,select,textarea,a,label,svg')) return;
                                                            handleSignatureMouseDown(e, sig.id);
                                                        }}
                                                        onTouchStart={(e: any) => {
                                                            const touch = (e as React.TouchEvent).touches && (e as React.TouchEvent).touches[0];
                                                            const tgt = touch ? (document.elementFromPoint(touch.clientX, touch.clientY) as HTMLElement) : null;
                                                            if (tgt && tgt.closest && tgt.closest('button,input,select,textarea,a,label,svg')) return;
                                                            handleSignatureMouseDown(e, sig.id);
                                                        }}
                                                    >
                                                        {/* Image + overlaid text container */}
                                                        <div className="relative" style={{ width: holderWidth, height: holderHeight - 20, overflow: 'visible' }}>
                                                            {/* Signature image (independently sized, not constrained by holder) */}
                                                            <div style={{ position: 'absolute', left: 0, top: 0, width: imgWidth, height: imgHeight, pointerEvents: 'none' }}>
                                                                {sig.isSpecimen ? (
                                                                    <img src="/specimen-placeholder.png" alt="specimen" style={{ width: imgWidth, height: imgHeight, objectFit: 'contain' }} />
                                                                ) : loadingSignature ? (
                                                                    <Skeleton style={{ width: imgWidth, height: imgHeight }} className="rounded bg-transparent" />
                                                                ) : errorSignature ? (
                                                                    <div className="text-xs text-destructive">Failed to load signature</div>
                                                                ) : signatureImageUrl ? (
                                                                    <img src={signatureImageUrl} alt="signature" style={{ width: imgWidth, height: imgHeight, objectFit: 'contain' }} />
                                                                ) : (
                                                                    <Skeleton style={{ width: imgWidth, height: imgHeight }} className="rounded bg-transparent" />
                                                                )}
                                                            </div>
                                                            {/* Text overlay (positioned inside, left-aligned, fixed width) */}
                                                            <div
                                                                className="absolute leading-tight"
                                                                style={{
                                                                    left: 95,
                                                                    top: textTop,
                                                                    width: holderWidth - 8,
                                                                    fontSize: textFontSize,
                                                                    pointerEvents: 'none',
                                                                    whiteSpace: 'nowrap',
                                                                    overflow: 'hidden',
                                                                    textOverflow: 'ellipsis',
                                                                }}
                                                            >
                                                                {authorityLevel === "3" ? (
                                                                    <>
                                                                        <div className="font-bold">BY AUTHORITY OF THE GOVERNOR</div>
                                                                        <div className="mt-1">Digitally signed by:</div>
                                                                        <div className="font-semibold mt-1">{baseFULLNAME ?? ''}</div>
                                                                        {!dateDisplay && (
                                                                            <div className="mt-0">
                                                                                Date: {new Date().toLocaleDateString('en-US', {
                                                                                    month: 'short',
                                                                                    day: 'numeric',
                                                                                    year: 'numeric'
                                                                                })}
                                                                            </div>
                                                                        )}
                                                                    </>
                                                                ) : authorityLevel === "4" ? (
                                                                    <>
                                                                        <div className="font-bold">FOR</div>
                                                                        <div className="mt-2">Digitally signed by:</div>
                                                                        <div className="font-bold mt-1">{baseFULLNAME ?? ''}</div>
                                                                        {!dateDisplay && (
                                                                            <div className="mt-0">
                                                                                Date: {new Date().toLocaleDateString('en-US', {
                                                                                    month: 'short',
                                                                                    day: 'numeric',
                                                                                    year: 'numeric'
                                                                                })}
                                                                            </div>
                                                                        )}
                                                                    </>
                                                                ) : (
                                                                    <>
                                                                        <div>Digitally signed by:</div>
                                                                        <div className="font-semibold mt-1">{baseFULLNAME ?? ''}</div>
                                                                        {!dateDisplay && (
                                                                            <div className="mt-0">
                                                                                Date: {new Date().toLocaleDateString('en-US', {
                                                                                    month: 'short',
                                                                                    day: 'numeric',
                                                                                    year: 'numeric'
                                                                                })}
                                                                            </div>
                                                                        )}
                                                                    </>
                                                                )}

                                                            </div>
                                                        </div>
                                                        <div className="flex items-center bg-primary text-primary-foreground px-1 py-0.5 rounded-b-md">
                                                            <Switch
                                                                checked={sig.isSpecimen}
                                                                onCheckedChange={() => toggleSpecimen(sig.id)}
                                                                className="scale-75 origin-left"
                                                            />
                                                            {/* Drag Handle */}
                                                            <span className="flex-1 text-center text-[8px] cursor-move select-none" style={{ pointerEvents: 'auto', touchAction: 'none' }}>
                                                                <span className={sig.isDragging ? 'font-semibold' : ''}>Drag to Move</span>
                                                            </span>
                                                            <Button
                                                                variant="ghost"
                                                                size="icon"
                                                                className="h-5 w-5 text-primary-foreground hover:text-primary-foreground hover:bg-primary-foreground/20"
                                                                onClick={(e) => {
                                                                    e.stopPropagation();
                                                                    deleteSignature(sig.id);
                                                                }}
                                                            >
                                                                <Trash2 className="h-3 w-3" />
                                                            </Button>
                                                        </div>
                                                    </div>
                                                );
                                            })}



                                    </div>
                                ))}
                            </Document>
                        )}
                    </div>
                </ScrollArea>

                {/* Mobile backdrop */}
                {sidebarOpen && (
                    <div
                        className="fixed inset-0 bg-black/40 z-998 sm:hidden"
                        onClick={() => setSidebarOpen(false)}
                    />
                )}
                {/* Right sidebar — mobile: right-side drawer, desktop: inline panel */}
                <div className={[
                    'fixed right-0 top-0 h-full z-999 w-72 border-l bg-card pt-16 px-6 pb-6 overflow-y-auto shadow-xl',
                    'transform transition-transform duration-300 ease-in-out',
                    'sm:relative sm:h-auto sm:z-auto sm:w-80 sm:pt-6 sm:transform-none sm:transition-none',
                    sidebarOpen ? 'translate-x-0 sm:block' : 'translate-x-full sm:hidden',
                ].join(' ')}>
                    <div className="flex items-center justify-between mb-6">
                        <h3 className="text-lg font-bold text-card-foreground uppercase tracking-wide">Document Info</h3>
                        <Button variant="ghost" size="icon" className="sm:hidden -mr-2" onClick={() => setSidebarOpen(false)}>
                            <X className="h-4 w-4" />
                        </Button>
                    </div>
                    <div className="space-y-4">
                        <div>
                            <span className="text-xs text-muted-foreground">Document ID</span>
                            <p className="text-base font-bold text-card-foreground bg-muted rounded px-2 py-1 w-fit">{doc_id}</p>
                        </div>
                        <div>
                            <span className="text-xs text-muted-foreground">Description</span>
                            <p className="text-base font-medium text-card-foreground bg-muted rounded px-2 py-1 w-fit">{typeof docDescription === "string" ? docDescription : "—"}</p>
                        </div>
                        <div>
                            <span className="text-xs text-muted-foreground">Status</span>
                            <p className="text-base font-medium text-card-foreground bg-muted rounded px-2 py-1 w-fit">{typeof docStatus === "string" ? docStatus : "—"}</p>
                        </div>
                    </div>
                </div>
            </div>

            {/* Fixed bottom bar – hidden in view-only mode */}
            {!viewOnly && <div className="fixed bottom-0 left-0 right-0 bg-background/95 backdrop-blur-sm border-t shadow-lg z-50 px-3 py-2">
                <div className="overflow-x-auto scrollbar-none">
                    <div className="flex items-center gap-3 min-w-max mx-auto py-0.5">
                        {/* Multi‑sign switch */}
                        <div className="flex items-center gap-2">
                            <Switch
                                checked={signMultiple}
                                onCheckedChange={setSignMultiple}
                                id="multi-sign"
                            />
                            <label htmlFor="multi-sign" className="text-sm whitespace-nowrap">
                                Sign Multiple Pages
                            </label>
                        </div>

                        {/* Page selector (conditional) */}
                        {signMultiple && (
                            <DropdownMenu>
                                <DropdownMenuTrigger>
                                    <span className="inline-flex items-center justify-center h-7 px-2.5 rounded-[min(var(--radius-md),12px)] text-[0.8rem] border border-border bg-background hover:bg-muted hover:text-foreground cursor-pointer select-none">
                                        Select Pages ({selectedPages.length})
                                    </span>
                                </DropdownMenuTrigger>
                                <DropdownMenuContent>
                                    <DropdownMenuItem onSelect={() => toggleAllPages(false)}>
                                        Uncheck All
                                    </DropdownMenuItem>
                                    {Array.from({ length: numPages }, (_, i) => i + 1).map((page) => (
                                        <DropdownMenuCheckboxItem
                                            key={page}
                                            checked={selectedPages.includes(page)}
                                            onCheckedChange={(checked: boolean) => {
                                                setSelectedPages((prev) =>
                                                    checked ? [...prev, page] : prev.filter((p) => p !== page)
                                                );
                                            }}
                                        >
                                            Page {page}
                                        </DropdownMenuCheckboxItem>
                                    ))}
                                </DropdownMenuContent>
                            </DropdownMenu>
                        )}

                        {/* Signature count */}
                        <div className="flex items-center gap-1 bg-muted px-2 py-1 rounded-full text-sm text-foreground font-semibold">
                            <span>{signatures.length}</span>
                            <span>/</span>
                            <span>{expectedSignCount}</span>
                        </div>

                        {/* History */}
                        <Button variant="ghost" size="icon" title="History">
                            <History className="h-4 w-4" />
                        </Button>

                        {/* Settings dropdown */}
                        <DropdownMenu>
                            <DropdownMenuTrigger>
                                <span className="inline-flex items-center justify-center h-8 w-8 rounded-lg hover:bg-muted cursor-pointer select-none">
                                    <Settings className="h-4 w-4" />
                                </span>
                            </DropdownMenuTrigger>
                            <DropdownMenuContent>
                                <DropdownMenuItem>Cancel PDF</DropdownMenuItem>
                            </DropdownMenuContent>
                        </DropdownMenu>

                        {/* Authority level — VVIP-only override (vip_no_date membership,
                            checked via isVip). Normally sig_level is fixed by the
                            document uploader; VVIP signers can freely switch the
                            stamp wording here. Drives the same `authorityLevel`
                            state as the preview and the save payload, so what's
                            shown is always what gets saved. Everyone else just
                            signs with the document's configured sig_level. */}
                        {isVip && (
                            <Select
                                items={[
                                    { value: "1", label: "Standard" },
                                    { value: "3", label: "By Authority of the Governor" },
                                    { value: "4", label: "For" },
                                ]}
                                value={authorityLevel}
                                onValueChange={(value) => {
                                    if (typeof value === "string") setAuthorityLevel(value);
                                }}
                            >
                                <SelectTrigger className="w-40">
                                    <SelectValue placeholder="Authority Level" />
                                </SelectTrigger>
                                <SelectContent>
                                    <SelectItem value="1">Standard</SelectItem>
                                    <SelectItem value="3">By Authority of the Governor</SelectItem>
                                    <SelectItem value="4">For</SelectItem>
                                </SelectContent>
                            </Select>
                        )}

                        {/* Date display toggle — VVIP-only, same gate as above */}
                        {isVip && (
                            <div className="flex items-center gap-1">
                                <Switch
                                    checked={dateDisplay}
                                    onCheckedChange={setDateDisplay}
                                    id="date-display"
                                />
                                <label htmlFor="date-display" className="text-sm whitespace-nowrap">
                                    Hide Signature Date
                                </label>
                            </div>
                        )}
                    </div>
                </div>
            </div>}

            {/* Floating action buttons – hidden in view-only mode */}
            {!viewOnly && (
                <div className="fixed bottom-24 right-6 flex flex-col gap-3 z-50">

                    <Button
                        size="icon"
                        variant="secondary"
                        className="rounded-full h-12 w-12 shadow-xl bg-green-600 hover:bg-green-700 text-white"
                        onClick={handleSave}
                        disabled={saving || checkingPfx}
                    >
                        {saving || checkingPfx ? <Loader2 className="h-5 w-5 animate-spin" /> : <Save className="h-5 w-5" />}
                    </Button>
                    <Button
                        size="icon"
                        variant="secondary"
                        className="rounded-full h-12 w-12 shadow-xl"
                        onClick={() => setAddMode(true)}
                    >
                        <Plus className="h-5 w-5" />
                    </Button>

                    <Button
                        size="icon"
                        variant="destructive"
                        className="rounded-full h-12 w-12 shadow-xl"
                        onClick={() => setSignatures((prev) => prev.slice(0, -1))}
                    >
                        <Trash2 className="h-5 w-5" />
                    </Button>
                    <Button
                        size="icon"
                        variant="outline"
                        className="rounded-full h-12 w-12 shadow-xl relative"
                    >
                        <Paperclip className="h-5 w-5" />
                        <span className="absolute -top-1 -right-1 bg-destructive text-destructive-foreground text-xs rounded-full h-5 w-5 flex items-center justify-center">
                            3
                        </span>
                    </Button>
                    <div className="rounded-full h-12 w-12 bg-muted flex items-center justify-center text-lg font-bold border text-foreground">
                        {sigSignCount}
                    </div>
                </div>
            )}

            {/* Signature-count mismatch confirm — always resolved/closed before
                the verify dialog below can open, so the two never overlap. */}
            <Dialog
                open={!!mismatchDialog}
                onOpenChange={(v) => {
                    if (!v) {
                        mismatchDialog?.resolve(false);
                        setMismatchDialog(null);
                    }
                }}
            >
                <DialogContent className="max-w-md">
                    <DialogHeader>
                        <DialogTitle className="flex items-center gap-2">
                            <AlertTriangle className="h-5 w-5 text-amber-500" />
                            Signature count mismatch
                        </DialogTitle>
                    </DialogHeader>
                    <p className="text-sm text-muted-foreground">
                        {mismatchDialog?.message} Are you sure you want to continue?
                    </p>
                    <DialogFooter>
                        <Button
                            variant="outline"
                            onClick={() => { mismatchDialog?.resolve(false); setMismatchDialog(null); }}
                        >
                            Cancel
                        </Button>
                        <Button
                            onClick={() => { mismatchDialog?.resolve(true); setMismatchDialog(null); }}
                        >
                            Continue
                        </Button>
                    </DialogFooter>
                </DialogContent>
            </Dialog>

            {/* Signature Verification Dialog */}
            <SignatureVerifyDialog
                open={verifyDialogOpen}
                onOpenChange={setVerifyDialogOpen}
                hasPfxPassword={hasPfxPassword}
                cpNumber={baseCP}
                email={baseEMAIL}
                onVerify={handleVerifyAndSign}
                loading={saving}
            />
        </div>
    );
}