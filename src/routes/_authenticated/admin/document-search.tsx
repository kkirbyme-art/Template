import * as React from "react"
import { createFileRoute } from "@tanstack/react-router"
import { toast } from "sonner"
import { apiUrl, authFetch } from "@/lib/config"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select"
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
} from "@/components/ui/card"
import {
    Collapsible,
    CollapsibleContent,
    CollapsibleTrigger,
} from "@/components/ui/collapsible"
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from "@/components/ui/table"
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogDescription,
    DialogFooter,
} from "@/components/ui/dialog"
import { UploadDropzone } from "@/components/upload-dropzone"
import DynamicSelect from "@/View/dynamic/d_select"
import DynamicMultiSelect, { type DynamicMultiModel } from "@/View/dynamic/DynamicMultiSelect"
import {
    ShieldAlert,
    Loader2,
    Search,
    ShieldCheck,
    Upload,
    X,
    Trash2,
    Eye,
    Building2,
    FileText,
    CheckCircle2,
    CalendarDays,
    Calendar,
    Filter,
    ChevronDown,
    RotateCw,
    AlertTriangle,
} from "lucide-react"

export const Route = createFileRoute("/_authenticated/admin/document-search")({
    component: DocumentSearchPage,
})

type SearchField = "doc_id" | "description" | "doc_code" | "all"
type DateFilterMode = "year" | "range"
type EmployeeRole = "uploaded" | "signed"

const EMPLOYEE_ROLES: { value: EmployeeRole; label: string }[] = [
    { value: "uploaded", label: "Uploaded" },
    { value: "signed", label: "Signed" },
]

const SEARCH_FIELDS: { value: SearchField; label: string }[] = [
    { value: "doc_id", label: "Doc ID" },
    { value: "description", label: "Description" },
    { value: "doc_code", label: "Doc Code" },
    { value: "all", label: "All Documents" },
]

interface AdminDocumentSearchResult {
    docId: number
    docName: string | null
    docDescription: string | null
    docCode: string | null
    docStatusId: number
    docTypeId: number | null
    docEid: number
    docEidUserType: number
    docDatetime: string | null
    docDatetimeUpdate: string | null
    documentTypeName: string | null
    statusType: string | null
    ownerName: string | null
    ownerOffice: string | null
}

interface DocStatusOption {
    id: number
    statusType: string
}

interface DocTypeOption {
    id: number
    documentDescription: string
}

interface SignatoryRow {
    id: string | number      // React key: numeric sigId for existing rows, "new-<eid>" for unsaved additions
    sigId: number | null     // null until this row is saved
    name: string
    abbrValue?: string
    eid: number
    userType: number
    statusId: number
    numSignatures: number
    order: number
    level: number
}

interface AdminSignatoryAuditRow {
    id: number
    docId: number
    sigId: number | null
    action: string
    changedByEid: string
    changedByName: string | null
    signatorySnapshot: string | null
    locationSnapshot: string | null
    changedAt: string
}

// doc_status_id values GetPdfDigitalOnlyAsync serves watermarked, and
// ReconstructSignedPdfAsync refuses outright — mirrors
// SigningService.NonReconstructableStatuses.
const NON_RECONSTRUCTABLE_STATUSES = [4, 5, 7, 8, 9, 13]

function DocumentSearchPage() {
    const [checkingAccess, setCheckingAccess] = React.useState(true)
    const [isAdmin, setIsAdmin] = React.useState(false)

    // Filters — same shape/style as My Documents (mydocument.tsx)
    const [selectedOffice, setSelectedOffice] = React.useState<string | null>(null)
    const [selectedDocTypeId, setSelectedDocTypeId] = React.useState<number | null>(null)
    const [selectedStatusId, setSelectedStatusId] = React.useState<number | null>(null)
    const [selectedEmployeeEid, setSelectedEmployeeEid] = React.useState<string | null>(null)
    const [employeeRole, setEmployeeRole] = React.useState<EmployeeRole>("uploaded")
    const [field, setField] = React.useState<SearchField>("all")
    const [query, setQuery] = React.useState("")

    const currentYear = new Date().getFullYear()
    const yearOptions = Array.from({ length: 10 }, (_, i) => currentYear - i)
    const defaultRange = () => {
        const now = new Date()
        return {
            startDate: new Date(now.getFullYear(), 0, 1).toISOString().slice(0, 10),
            endDate: new Date(now.getFullYear(), 11, 31).toISOString().slice(0, 10),
        }
    }
    const dr = defaultRange()
    const [dateFilterMode, setDateFilterMode] = React.useState<DateFilterMode>("year")
    const [selectedYear, setSelectedYear] = React.useState<number>(currentYear)
    const [startDate, setStartDate] = React.useState<string>(dr.startDate)
    const [endDate, setEndDate] = React.useState<string>(dr.endDate)
    const [useDateFilter, setUseDateFilter] = React.useState(false)
    const [filtersOpen, setFiltersOpen] = React.useState(true)

    const [results, setResults] = React.useState<AdminDocumentSearchResult[] | null>(null)
    const [searching, setSearching] = React.useState(false)
    const [viewingDoc, setViewingDoc] = React.useState<AdminDocumentSearchResult | null>(null)
    const [statusOptions, setStatusOptions] = React.useState<DocStatusOption[]>([])
    const [typeOptions, setTypeOptions] = React.useState<DocTypeOption[]>([])

    React.useEffect(() => {
        let cancelled = false
            ; (async () => {
                setCheckingAccess(true)
                try {
                    const res = await authFetch(apiUrl("DGSign/check_admin"))
                    const data = await res.json().catch(() => null)
                    if (!cancelled) setIsAdmin(!!data?.isAdmin)
                } catch {
                    if (!cancelled) setIsAdmin(false)
                } finally {
                    if (!cancelled) setCheckingAccess(false)
                }
            })()
        return () => { cancelled = true }
    }, [])

    React.useEffect(() => {
        if (!isAdmin) return
            ; (async () => {
                try {
                    const [statusRes, typeRes] = await Promise.all([
                        authFetch(apiUrl("References/get_doc_statuses")),
                        authFetch(apiUrl("References/get_document_types")),
                    ])
                    if (statusRes.ok) setStatusOptions(await statusRes.json())
                    if (typeRes.ok) {
                        const types = await typeRes.json()
                        setTypeOptions(
                            (types ?? []).map((t: { id: string | number; value: string }) => ({
                                id: Number(t.id),
                                documentDescription: t.value,
                            }))
                        )
                    }
                } catch {
                    // dropdowns just stay empty — non-fatal
                }
            })()
    }, [isAdmin])

    const handleSearch = async () => {
        setSearching(true)
        setFiltersOpen(false)
        try {
            const params = new URLSearchParams({ field, query })
            if (selectedOffice) params.append("officeId", selectedOffice)
            if (selectedDocTypeId != null) params.append("docTypeId", String(selectedDocTypeId))
            if (selectedStatusId != null) params.append("statusId", String(selectedStatusId))
            if (selectedOffice && selectedEmployeeEid) {
                params.append("employeeEid", selectedEmployeeEid)
                params.append("employeeRole", employeeRole)
            }
            if (useDateFilter) {
                if (dateFilterMode === "year") {
                    params.append("year", String(selectedYear))
                } else {
                    params.append("startDate", startDate)
                    params.append("endDate", endDate)
                }
            }

            const res = await authFetch(apiUrl(`DGSign/admin_search_documents?${params}`))
            if (!res.ok) throw new Error("Search failed")
            const data: AdminDocumentSearchResult[] = await res.json()
            setResults(data)
        } catch {
            toast.error("Failed to search documents.")
        } finally {
            setSearching(false)
        }
    }

    const handleReset = () => {
        setSelectedOffice(null)
        setSelectedDocTypeId(null)
        setSelectedStatusId(null)
        setSelectedEmployeeEid(null)
        setEmployeeRole("uploaded")
        setField("all")
        setQuery("")
        setUseDateFilter(false)
        setDateFilterMode("year")
        setSelectedYear(currentYear)
        const r = defaultRange()
        setStartDate(r.startDate)
        setEndDate(r.endDate)
        setResults(null)
    }

    if (checkingAccess) {
        return (
            <div className="flex min-h-100 items-center justify-center">
                <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
            </div>
        )
    }

    if (!isAdmin) {
        return (
            <div className="mx-auto flex min-h-100 w-full max-w-md flex-col items-center justify-center gap-3 text-center">
                <div className="rounded-full bg-destructive/10 p-3">
                    <ShieldAlert className="h-6 w-6 text-destructive" />
                </div>
                <h1 className="text-lg font-semibold">Not authorized</h1>
                <p className="text-sm text-muted-foreground">
                    You don't have access to document search/override.
                </p>
            </div>
        )
    }

    return (
        <div className="mx-auto w-full max-w-6xl space-y-6">
            <div>
                <h1 className="text-2xl font-bold tracking-tight flex items-center gap-2">
                    <ShieldCheck className="h-6 w-6 text-primary" />
                    Document Search
                </h1>
                <p className="text-sm text-muted-foreground">
                    Find any document and override its metadata, PDF, or signatories —
                    bypasses the normal signed-document edit lock.
                </p>
            </div>

            {/* ── Filter Card (same style as My Documents) ── */}
            <Card className="shadow-sm border-border gap-0 py-0">
                <Collapsible open={filtersOpen} onOpenChange={setFiltersOpen} className="group/filters">
                    <CollapsibleTrigger
                        className="flex w-full items-center gap-2 px-5 py-2.5 border-b hover:bg-muted/50 transition-colors"
                    >
                        <div className="p-1.5 rounded-md bg-primary/10">
                            <Filter className="h-4 w-4 text-primary" />
                        </div>
                        <span className="font-semibold text-sm">Filter Options</span>
                        {!filtersOpen && (
                            <span className="text-xs text-muted-foreground font-normal truncate">
                                — tap to edit
                            </span>
                        )}
                        <ChevronDown className="h-4 w-4 text-muted-foreground ml-auto transition-transform duration-200 group-data-[state=open]/filters:rotate-180" />
                    </CollapsibleTrigger>

                    <CollapsibleContent>
                        <CardContent className="p-4 sm:p-5 space-y-4">
                            {/* Row 1 — Office + Document Type */}
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div className="space-y-2">
                                    <Label className="flex items-center gap-1.5 text-sm font-medium">
                                        <Building2 className="h-3.5 w-3.5 text-muted-foreground" />
                                        Office
                                        <span className="ml-1 text-xs text-muted-foreground font-normal">(optional)</span>
                                    </Label>
                                    <DynamicSelect
                                        api={apiUrl("references/get_offices")}
                                        placeholder="All offices"
                                        value={selectedOffice ?? ""}
                                        defaultvalue=""
                                        onchange={true}
                                        onChangeCallback={(id) => {
                                            setSelectedOffice(id ? String(id) : null)
                                            setSelectedEmployeeEid(null)
                                        }}
                                        classValue="w-full"
                                        containerClass="w-full"
                                    />
                                    {selectedOffice && (
                                        <div className="space-y-2 pt-1">
                                            <DynamicSelect
                                                api={apiUrl("References/get_employees_by_office")}
                                                parametername="officeId"
                                                parametervalue={selectedOffice}
                                                placeholder="Any employee in this office"
                                                value={selectedEmployeeEid ?? ""}
                                                defaultvalue=""
                                                onchange={true}
                                                onChangeCallback={(id) => setSelectedEmployeeEid(id ? String(id) : null)}
                                                classValue="w-full"
                                                containerClass="w-full"
                                            />
                                            {selectedEmployeeEid && (
                                                <RadioGroup
                                                    value={employeeRole}
                                                    onValueChange={(v) => setEmployeeRole(v as EmployeeRole)}
                                                    className="flex flex-wrap items-center gap-4"
                                                >
                                                    {EMPLOYEE_ROLES.map((opt) => (
                                                        <label key={opt.value} className="flex items-center gap-1.5 text-xs cursor-pointer text-muted-foreground">
                                                            <RadioGroupItem value={opt.value} />
                                                            {opt.label}
                                                        </label>
                                                    ))}
                                                </RadioGroup>
                                            )}
                                        </div>
                                    )}
                                </div>
                                <div className="space-y-2">
                                    <Label className="flex items-center gap-1.5 text-sm font-medium">
                                        <FileText className="h-3.5 w-3.5 text-muted-foreground" />
                                        Document Type
                                        <span className="ml-1 text-xs text-muted-foreground font-normal">(optional)</span>
                                    </Label>
                                    <DynamicSelect
                                        api={apiUrl("References/get_document_types")}
                                        placeholder="All document types"
                                        value={selectedDocTypeId !== null ? String(selectedDocTypeId) : ""}
                                        defaultvalue=""
                                        onchange={true}
                                        onChangeCallback={(id) => {
                                            const numericId = Number(id)
                                            setSelectedDocTypeId(Number.isNaN(numericId) || !id ? null : numericId)
                                        }}
                                        classValue="w-full"
                                        containerClass="w-full"
                                    />
                                </div>
                            </div>

                            {/* Row 2 — Status + free-text search */}
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div className="space-y-2">
                                    <Label className="flex items-center gap-1.5 text-sm font-medium">
                                        <CheckCircle2 className="h-3.5 w-3.5 text-muted-foreground" />
                                        Document Status
                                        <span className="ml-1 text-xs text-muted-foreground font-normal">(optional)</span>
                                    </Label>
                                    <Select
                                        value={selectedStatusId !== null ? String(selectedStatusId) : "__any__"}
                                        onValueChange={(v) => setSelectedStatusId(v === "__any__" ? null : Number(v))}
                                    >
                                        <SelectTrigger className="w-full">
                                            <SelectValue placeholder="Any status" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="__any__">Any status</SelectItem>
                                            {statusOptions.map((s) => (
                                                <SelectItem key={s.id} value={String(s.id)}>
                                                    {s.statusType}
                                                </SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>
                                <div className="space-y-2">
                                    <Label className="text-sm font-medium">Search text</Label>
                                    <div className="flex gap-2">
                                        <Input
                                            value={query}
                                            onChange={(e) => setQuery(e.target.value)}
                                            onKeyDown={(e) => { if (e.key === "Enter") handleSearch() }}
                                            placeholder="Matched as %query%"
                                            className="flex-1"
                                        />
                                    </div>
                                    <RadioGroup value={field} onValueChange={(v) => setField(v as SearchField)} className="flex flex-wrap items-center gap-4 pt-1">
                                        {SEARCH_FIELDS.map((opt) => (
                                            <label key={opt.value} className="flex items-center gap-1.5 text-xs cursor-pointer text-muted-foreground">
                                                <RadioGroupItem value={opt.value} />
                                                {opt.label}
                                            </label>
                                        ))}
                                    </RadioGroup>
                                </div>
                            </div>

                            {/* Row 3 — Date filter (optional, toggleable) */}
                            <div className="space-y-3">
                                <label className="flex items-center gap-2 text-sm font-medium cursor-pointer w-fit">
                                    <input
                                        type="checkbox"
                                        checked={useDateFilter}
                                        onChange={(e) => setUseDateFilter(e.target.checked)}
                                        className="accent-primary"
                                    />
                                    <CalendarDays className="h-3.5 w-3.5 text-muted-foreground" />
                                    Filter by date
                                </label>

                                {useDateFilter && (
                                    <>
                                        <div className="flex gap-2 flex-wrap">
                                            <button
                                                type="button"
                                                onClick={() => setDateFilterMode("year")}
                                                className={`inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-all ${dateFilterMode === "year"
                                                    ? "bg-primary text-primary-foreground border-primary"
                                                    : "border-input bg-background text-muted-foreground hover:bg-muted hover:text-foreground"
                                                    }`}
                                            >
                                                <CalendarDays className="h-3.5 w-3.5" />
                                                By Year
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => setDateFilterMode("range")}
                                                className={`inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-all ${dateFilterMode === "range"
                                                    ? "bg-primary text-primary-foreground border-primary"
                                                    : "border-input bg-background text-muted-foreground hover:bg-muted hover:text-foreground"
                                                    }`}
                                            >
                                                <Calendar className="h-3.5 w-3.5" />
                                                By Date Range
                                            </button>
                                        </div>

                                        {dateFilterMode === "year" ? (
                                            <div className="w-full sm:w-40">
                                                <Select value={String(selectedYear)} onValueChange={(v) => setSelectedYear(Number(v))}>
                                                    <SelectTrigger className="w-full">
                                                        <CalendarDays className="h-4 w-4 mr-1.5 text-muted-foreground" />
                                                        <SelectValue placeholder="Select year" />
                                                    </SelectTrigger>
                                                    <SelectContent>
                                                        {yearOptions.map((y) => (
                                                            <SelectItem key={y} value={String(y)}>{y}</SelectItem>
                                                        ))}
                                                    </SelectContent>
                                                </Select>
                                            </div>
                                        ) : (
                                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                                                <div className="space-y-1.5">
                                                    <Label htmlFor="admin-start-date" className="text-xs text-muted-foreground">From</Label>
                                                    <Input id="admin-start-date" type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
                                                </div>
                                                <div className="space-y-1.5">
                                                    <Label htmlFor="admin-end-date" className="text-xs text-muted-foreground">To</Label>
                                                    <Input id="admin-end-date" type="date" value={endDate} onChange={(e) => setEndDate(e.target.value)} />
                                                </div>
                                            </div>
                                        )}
                                    </>
                                )}
                            </div>

                            {/* Action buttons */}
                            <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-3 pt-2 border-t">
                                <Button onClick={handleSearch} disabled={searching} className="flex items-center gap-2 sm:flex-none">
                                    {searching ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                                    Search
                                </Button>
                                <Button onClick={handleReset} variant="outline" className="flex items-center gap-2 sm:flex-none">
                                    <RotateCw className="h-4 w-4" />
                                    Reset Filters
                                </Button>
                            </div>
                        </CardContent>
                    </CollapsibleContent>
                </Collapsible>
            </Card>

            {results !== null && (
                <Card>
                    <CardHeader>
                        <CardTitle className="text-base">Results</CardTitle>
                        <CardDescription>
                            {results.length} {results.length === 1 ? "document" : "documents"}
                        </CardDescription>
                    </CardHeader>
                    <CardContent>
                        {searching ? (
                            <div className="space-y-2">
                                <Skeleton className="h-10 w-full" />
                                <Skeleton className="h-10 w-full" />
                            </div>
                        ) : results.length === 0 ? (
                            <p className="py-8 text-center text-sm text-muted-foreground">
                                No documents matched your search.
                            </p>
                        ) : (
                            <div className="rounded-lg border overflow-hidden">
                                <div className="overflow-x-auto">
                                    <Table>
                                        <TableHeader>
                                            <TableRow className="bg-muted/40 hover:bg-muted/40">
                                                <TableHead>Doc ID</TableHead>
                                                <TableHead>Description</TableHead>
                                                <TableHead>Doc Code</TableHead>
                                                <TableHead>Status</TableHead>
                                                <TableHead>Type</TableHead>
                                                <TableHead>Owner</TableHead>
                                                <TableHead className="text-center">View</TableHead>
                                            </TableRow>
                                        </TableHeader>
                                        <TableBody>
                                            {results.map((row) => (
                                                <TableRow key={row.docId} className="hover:bg-muted/30 transition-colors">
                                                    <TableCell className="text-sm">{row.docId}</TableCell>
                                                    <TableCell className="text-sm max-w-60 truncate" title={row.docDescription ?? undefined}>
                                                        {row.docDescription || "—"}
                                                    </TableCell>
                                                    <TableCell className="text-sm text-muted-foreground">{row.docCode || "—"}</TableCell>
                                                    <TableCell>
                                                        <Badge variant="outline" className="text-[10px]">{row.statusType || row.docStatusId}</Badge>
                                                    </TableCell>
                                                    <TableCell className="text-sm">{row.documentTypeName || "—"}</TableCell>
                                                    <TableCell className="text-sm">
                                                        <div className="flex flex-col">
                                                            <span>{row.ownerName || row.docEid}</span>
                                                            {row.ownerOffice && (
                                                                <span className="text-xs text-muted-foreground">{row.ownerOffice}</span>
                                                            )}
                                                        </div>
                                                    </TableCell>
                                                    <TableCell className="text-center">
                                                        <Button
                                                            variant="ghost"
                                                            size="icon"
                                                            className="h-7 w-7 rounded-full hover:bg-primary/10 text-muted-foreground hover:text-primary"
                                                            title="View / edit document"
                                                            onClick={() => setViewingDoc(row)}
                                                        >
                                                            <Eye className="h-4 w-4" />
                                                        </Button>
                                                    </TableCell>
                                                </TableRow>
                                            ))}
                                        </TableBody>
                                    </Table>
                                </div>
                            </div>
                        )}
                    </CardContent>
                </Card>
            )}

            <AdminDocumentViewDialog
                doc={viewingDoc}
                statusOptions={statusOptions}
                typeOptions={typeOptions}
                onClose={() => setViewingDoc(null)}
                onSaved={handleSearch}
            />
        </div>
    )
}

function AdminDocumentViewDialog({
    doc,
    statusOptions,
    typeOptions,
    onClose,
    onSaved,
}: {
    doc: AdminDocumentSearchResult | null
    statusOptions: DocStatusOption[]
    typeOptions: DocTypeOption[]
    onClose: () => void
    onSaved: () => void
}) {
    const [loading, setLoading] = React.useState(false)
    const [saving, setSaving] = React.useState(false)
    const [description, setDescription] = React.useState("")
    const [docCode, setDocCode] = React.useState("")
    const [statusId, setStatusId] = React.useState<string>("")
    const [typeId, setTypeId] = React.useState<string>("")
    const [pdfFile, setPdfFile] = React.useState<File | null>(null)
    const [pdfDragOver, setPdfDragOver] = React.useState(false)
    const pdfInputRef = React.useRef<HTMLInputElement>(null)

    const [rows, setRows] = React.useState<SignatoryRow[]>([])
    const [selectedIds, setSelectedIds] = React.useState<(string | number)[]>([])

    const [auditRows, setAuditRows] = React.useState<AdminSignatoryAuditRow[]>([])
    const [auditLoading, setAuditLoading] = React.useState(false)
    const [auditError, setAuditError] = React.useState<string | null>(null)
    const [auditOpen, setAuditOpen] = React.useState(false)
    const [auditReloadToken, setAuditReloadToken] = React.useState(0)

    const [sigStatusOptions, setSigStatusOptions] = React.useState<DocStatusOption[]>([])
    const [updatingSigId, setUpdatingSigId] = React.useState<number | null>(null)

    const [pdfUrl, setPdfUrl] = React.useState<string | null>(null)
    const [pdfLoading, setPdfLoading] = React.useState(false)
    const [pdfError, setPdfError] = React.useState<string | null>(null)
    const [pdfReloadToken, setPdfReloadToken] = React.useState(0)
    const [reconstructing, setReconstructing] = React.useState(false)

    const nonReconstructable = doc != null && NON_RECONSTRUCTABLE_STATUSES.includes(doc.docStatusId)

    React.useEffect(() => {
        authFetch(apiUrl("References/get_signatory_statuses"))
            .then((res) => (res.ok ? res.json() : []))
            .then(setSigStatusOptions)
            .catch(() => setSigStatusOptions([]))
    }, [])

    React.useEffect(() => {
        if (!doc) return
        let cancelled = false
        setDescription(doc.docDescription ?? "")
        setDocCode(doc.docCode ?? "")
        setStatusId(String(doc.docStatusId ?? ""))
        setTypeId(doc.docTypeId != null ? String(doc.docTypeId) : "")
        setPdfFile(null)

            ; (async () => {
                setLoading(true)
                try {
                    const res = await authFetch(apiUrl(`DGSign/get_document_for_edit?docId=${doc.docId}`))
                    if (!res.ok) throw new Error("Failed to load document")
                    const data = await res.json()
                    if (cancelled) return

                    const allSigs = data.signatories ?? []
                    const mapped: SignatoryRow[] = allSigs.map((s: any) => ({
                        id: s.sigId,
                        sigId: s.sigId,
                        name: s.fname || String(s.sigEid),
                        eid: s.sigEid,
                        userType: s.sigUserType,
                        statusId: s.sigStatus ?? 0,
                        numSignatures: s.sigSignCount ?? 1,
                        order: s.sigOrder ?? 1,
                        level: s.sigLevel ?? 1,
                    }))
                    setRows(mapped)
                    setSelectedIds(mapped.map((r) => r.id))
                } catch {
                    toast.error("Failed to load document details.")
                } finally {
                    if (!cancelled) setLoading(false)
                }
            })()
        return () => { cancelled = true }
    }, [doc])

    React.useEffect(() => {
        if (!doc) {
            setAuditRows([])
            setAuditError(null)
            return
        }
        let cancelled = false
        setAuditLoading(true)
        setAuditError(null)
        authFetch(apiUrl(`DGSign/admin_get_signatory_audit?docId=${doc.docId}`))
            .then((res: Response) => {
                if (!res.ok) throw new Error(`Failed to load history (${res.status})`)
                return res.json()
            })
            .then((data: AdminSignatoryAuditRow[]) => { if (!cancelled) setAuditRows(data ?? []) })
            .catch((err: unknown) => {
                if (!cancelled) setAuditError(err instanceof Error ? err.message : "Failed to load history")
            })
            .finally(() => { if (!cancelled) setAuditLoading(false) })
        return () => { cancelled = true }
    }, [doc, auditReloadToken])

    // PDF preview
    React.useEffect(() => {
        if (!doc) return
        let revoke: string | null = null
        let cancelled = false
        setPdfLoading(true)
        setPdfError(null)
        authFetch(apiUrl(`DGSign/get_pdf_digital_only?formId=${doc.docId}&isDownload=0`))
            .then((res) => {
                if (!res.ok) throw new Error(`Failed to load PDF (${res.status})`)
                return res.blob()
            })
            .then((blob) => {
                if (cancelled) return
                const url = URL.createObjectURL(blob)
                revoke = url
                setPdfUrl(url)
            })
            .catch((err: unknown) => { if (!cancelled) setPdfError(err instanceof Error ? err.message : "Failed to load PDF") })
            .finally(() => { if (!cancelled) setPdfLoading(false) })
        return () => { cancelled = true; if (revoke) URL.revokeObjectURL(revoke) }
    }, [doc, pdfReloadToken])

    React.useEffect(() => {
        if (!doc) {
            setPdfUrl(null)
            setPdfError(null)
            setPdfReloadToken(0)
        }
    }, [doc])

    const handleSignatoryChange = (ids: (string | number)[], items: DynamicMultiModel[]) => {
        setSelectedIds(ids)
        setRows((prev) => {
            const kept = prev.filter((row) => ids.some((id) => String(id) === String(row.id)))
            const keptIds = new Set(kept.map((r) => String(r.id)))
            let nextOrder = kept.length ? Math.max(...kept.map((r) => r.order)) + 1 : 1
            const added = items
                .filter((item) => !keptIds.has(String(item.id)))
                .map((item) => ({
                    id: `new-${item.id}`,
                    sigId: null,
                    name: item.value,
                    abbrValue: item.abbr_value,
                    eid: Number(item.additional_id ?? 0),
                    userType: Number((item as Record<string, unknown>)["additional_Id_two"] ?? 0),
                    statusId: 0,
                    numSignatures: 1,
                    order: nextOrder++,
                    level: 1,
                }))
            return [...kept, ...added]
        })
    }

    const removeRow = (id: string | number) => {
        setSelectedIds((prev) => prev.filter((s) => String(s) !== String(id)))
        setRows((prev) => prev.filter((row) => String(row.id) !== String(id)))
    }

    const updateRowOrder = (id: string | number, order: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, order } : row)))
    }

    const updateRowCount = (id: string | number, numSignatures: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, numSignatures } : row)))
    }

    // Status is applied immediately (this row already exists server-side);
    // order/count/delete/add stay local and go through the batch Save
    // below, same as before.
    const handleRowStatusChange = async (sigId: number, newStatusId: number) => {
        setUpdatingSigId(sigId)
        try {
            const res = await authFetch(apiUrl("DGSign/admin_update_signatory_status"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ SigId: sigId, Status: newStatusId }),
            })
            const data = await res.json().catch(() => null)
            if (!res.ok || !data?.success) throw new Error(data?.message ?? "Failed to update signatory status")

            setRows((prev) => prev.map((r) => (r.sigId === sigId ? { ...r, statusId: newStatusId } : r)))
            toast.success("Signatory status updated.")
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Failed to update signatory status.")
        } finally {
            setUpdatingSigId(null)
        }
    }

    // A brand-new row (no sigId yet) has nothing to PATCH — its status is
    // just local state until the batch Save inserts it.
    const updateRowStatus = (id: string | number, statusId: number) => {
        setRows((prev) => prev.map((row) => (String(row.id) === String(id) ? { ...row, statusId } : row)))
    }

    const handleForceReload = async () => {
        if (!doc) return
        setReconstructing(true)
        try {
            const res = await authFetch(apiUrl(`DGSign/reconstruct_pdf/${doc.docId}`), { method: "POST" })
            if (!res.ok) {
                const body = await res.json().catch(() => null)
                throw new Error(body?.message ?? `Reconstruction failed (${res.status})`)
            }
            toast.success("Document reconstructed from signature records.")
            setPdfReloadToken((c) => c + 1)
        } catch (err) {
            toast.error(err instanceof Error ? err.message : "Failed to reconstruct document.")
        } finally {
            setReconstructing(false)
        }
    }

    const handleSave = async () => {
        if (!doc) return
        setSaving(true)
        try {
            const meta = {
                DocId: doc.docId,
                Description: description,
                DocCode: docCode,
                DocStatusId: statusId ? Number(statusId) : null,
                DocTypeId: typeId ? Number(typeId) : null,
            }
            const formData = new FormData()
            formData.append("meta", JSON.stringify(meta))
            if (pdfFile) formData.append("pdfFile", pdfFile)

            const docRes = await authFetch(apiUrl("DGSign/admin_update_document"), {
                method: "POST",
                body: formData,
            })
            const docData = await docRes.json().catch(() => null)
            if (!docRes.ok || !docData?.success) throw new Error(docData?.message ?? "Failed to update document")

            const sigRes = await authFetch(apiUrl("DGSign/admin_update_signatories"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    DocId: doc.docId,
                    Signatories: rows.map((r) => ({
                        SigId: r.sigId,
                        Eid: r.eid,
                        UserType: r.userType,
                        Order: r.order,
                        NumSignatures: r.numSignatures,
                        Level: r.level,
                        Status: r.statusId,
                    })),
                }),
            })
            const sigData = await sigRes.json().catch(() => null)
            if (!sigRes.ok || !sigData?.success) throw new Error(sigData?.message ?? "Failed to update signatories")

            toast.success("Document updated.")
            if (pdfFile) setPdfReloadToken((c) => c + 1)
            onSaved()
            onClose()
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Failed to save changes.")
        } finally {
            setSaving(false)
        }
    }

    return (
        <Dialog open={!!doc} onOpenChange={(v) => { if (!v) onClose() }}>
            <DialogContent className="w-[98vw] h-[95vh] max-w-none sm:max-w-none p-0 gap-0 flex flex-col overflow-hidden">
                <DialogHeader className="shrink-0 border-b px-4 py-3">
                    <DialogTitle className="text-base">Document {doc ? `#${doc.docId}` : ""}</DialogTitle>
                    <DialogDescription className="text-xs">
                        Admin override — changes apply regardless of signing progress.
                    </DialogDescription>
                </DialogHeader>

                <div className="flex-1 min-h-0 flex flex-col lg:flex-row overflow-hidden">
                    {/* ── Left: edit controls ── */}
                    <div className="w-full lg:w-105 shrink-0 border-b lg:border-b-0 lg:border-r overflow-y-auto p-4">
                        {loading ? (
                            <div className="space-y-2">
                                <Skeleton className="h-10 w-full" />
                                <Skeleton className="h-10 w-full" />
                                <Skeleton className="h-32 w-full" />
                            </div>
                        ) : (
                            <div className="space-y-5">
                                <div className="flex items-center justify-between">
                                    <Label className="text-sm font-semibold">Reconstruct</Label>
                                    <Button
                                        size="sm"
                                        variant="outline"
                                        onClick={handleForceReload}
                                        disabled={reconstructing || nonReconstructable}
                                        title={
                                            nonReconstructable
                                                ? "This document is returned/cancelled/terminated and cannot be reconstructed."
                                                : "Redraw every signature on this document from its stored records and re-save it"
                                        }
                                    >
                                        {reconstructing ? <Loader2 className="h-4 w-4 mr-1.5 animate-spin" /> : <RotateCw className="h-4 w-4 mr-1.5" />}
                                        Force Reload
                                    </Button>
                                </div>

                                <div className="space-y-1.5">
                                    <Label>Description</Label>
                                    <Input value={description} onChange={(e) => setDescription(e.target.value)} />
                                </div>
                                <div className="space-y-1.5">
                                    <Label>Doc Code</Label>
                                    <Input value={docCode} disabled />
                                </div>
                                <div className="space-y-1.5">
                                    <Label>Document Status</Label>
                                    <Select value={statusId} onValueChange={(v) => setStatusId(v ?? "")}>
                                        <SelectTrigger className="w-full">
                                            <SelectValue placeholder="Select status" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            {statusOptions.map((s) => (
                                                <SelectItem key={s.id} value={String(s.id)}>
                                                    {s.statusType}
                                                </SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>
                                <div className="space-y-1.5">
                                    <Label>Document Type</Label>
                                    <Select value={typeId} onValueChange={(v) => setTypeId(v ?? "")}>
                                        <SelectTrigger className="w-full">
                                            <SelectValue placeholder="Select document type" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            {typeOptions.map((t) => (
                                                <SelectItem key={t.id} value={String(t.id)}>
                                                    {t.documentDescription}
                                                </SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>

                                <div className="space-y-1.5">
                                    <Label>Replace PDF (optional)</Label>
                                    {pdfFile ? (
                                        <div className="flex items-center gap-3 rounded-xl border bg-emerald-50 dark:bg-emerald-950/20 border-emerald-200 dark:border-emerald-800 px-3 py-2.5">
                                            <span className="text-sm flex-1 truncate">{pdfFile.name}</span>
                                            <Button type="button" variant="ghost" size="icon" className="h-8 w-8" onClick={() => setPdfFile(null)}>
                                                <X className="h-4 w-4" />
                                            </Button>
                                        </div>
                                    ) : (
                                        <UploadDropzone
                                            dragOver={pdfDragOver}
                                            icon={Upload}
                                            title="Drag & drop a replacement PDF"
                                            subtitle="or browse — PDF only"
                                            inputRef={pdfInputRef}
                                            accept="application/pdf"
                                            compact
                                            onDrop={(e) => {
                                                e.preventDefault()
                                                setPdfDragOver(false)
                                                const f = e.dataTransfer.files?.[0]
                                                if (f) setPdfFile(f)
                                            }}
                                            onDragOver={(e) => { e.preventDefault(); setPdfDragOver(true) }}
                                            onDragLeave={() => setPdfDragOver(false)}
                                            onChange={(e) => {
                                                const f = e.target.files?.[0]
                                                if (f) setPdfFile(f)
                                                e.target.value = ""
                                            }}
                                        />
                                    )}
                                </div>

                                <div className="space-y-3">
                                    <Label>Signatories</Label>

                                    <DynamicMultiSelect
                                        api={apiUrl("references/get_listofSignatories")}
                                        placeholder="Add signatories..."
                                        value={selectedIds}
                                        maxBadges={2}
                                        showSelectAll={false}
                                        displayRenderer={(item) => {
                                            const tag = item["additional_Id_two"] === "0" ? "PGAS" : "Non-PGAS"
                                            return `${item.value} (${item.abbr_value ?? ""}) (${tag})`
                                        }}
                                        onChangeCallback={handleSignatoryChange}
                                    />

                                    {rows.length > 0 && (
                                        <div className="rounded-lg border overflow-hidden">
                                            <div className="overflow-x-auto">
                                                <Table>
                                                    <TableHeader>
                                                        <TableRow className="bg-muted/40 hover:bg-muted/40">
                                                            <TableHead>Name</TableHead>
                                                            <TableHead className="w-36">Status</TableHead>
                                                            <TableHead className="text-center w-20">Count</TableHead>
                                                            <TableHead className="text-center w-20">Order</TableHead>
                                                            <TableHead className="text-center w-14">Remove</TableHead>
                                                        </TableRow>
                                                    </TableHeader>
                                                    <TableBody>
                                                        {rows.map((row) => (
                                                            <TableRow key={row.id}>
                                                                <TableCell className="text-sm">{row.name}</TableCell>
                                                                <TableCell>
                                                                    <Select
                                                                        value={String(row.statusId)}
                                                                        onValueChange={(v) =>
                                                                            row.sigId != null
                                                                                ? handleRowStatusChange(row.sigId, Number(v))
                                                                                : updateRowStatus(row.id, Number(v))
                                                                        }
                                                                        disabled={row.sigId != null && updatingSigId === row.sigId}
                                                                    >
                                                                        <SelectTrigger className="h-8 w-full text-xs">
                                                                            {row.sigId != null && updatingSigId === row.sigId ? (
                                                                                <Loader2 className="h-3 w-3 animate-spin" />
                                                                            ) : (
                                                                                <SelectValue />
                                                                            )}
                                                                        </SelectTrigger>
                                                                        <SelectContent>
                                                                            {sigStatusOptions.map((s) => (
                                                                                <SelectItem key={s.id} value={String(s.id)} className="text-xs">
                                                                                    {s.statusType}
                                                                                </SelectItem>
                                                                            ))}
                                                                        </SelectContent>
                                                                    </Select>
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.numSignatures}
                                                                        onChange={(e) => updateRowCount(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Input
                                                                        type="number"
                                                                        min={1}
                                                                        value={row.order}
                                                                        onChange={(e) => updateRowOrder(row.id, Math.max(1, Number(e.target.value) || 1))}
                                                                        className="h-8 w-14 text-center mx-auto"
                                                                    />
                                                                </TableCell>
                                                                <TableCell className="text-center">
                                                                    <Button type="button" variant="ghost" size="icon" className="h-8 w-8" onClick={() => removeRow(row.id)}>
                                                                        <Trash2 className="h-3.5 w-3.5 text-destructive" />
                                                                    </Button>
                                                                </TableCell>
                                                            </TableRow>
                                                        ))}
                                                    </TableBody>
                                                </Table>
                                            </div>
                                        </div>
                                    )}
                                </div>

                                <Collapsible open={auditOpen} onOpenChange={setAuditOpen}>
                                    <CollapsibleTrigger className="flex w-full items-center gap-2 text-sm font-semibold">
                                        History
                                        <ChevronDown className="h-4 w-4 text-muted-foreground ml-auto transition-transform data-[state=open]:rotate-180" />
                                    </CollapsibleTrigger>
                                    <CollapsibleContent className="pt-2">
                                        {auditLoading ? (
                                            <Skeleton className="h-16 w-full" />
                                        ) : auditError ? (
                                            <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
                                                <span>{auditError}</span>
                                                <Button size="sm" variant="outline" onClick={() => setAuditReloadToken((c) => c + 1)}>Retry</Button>
                                            </div>
                                        ) : auditRows.length === 0 ? (
                                            <p className="text-xs text-muted-foreground">No admin changes recorded for this document.</p>
                                        ) : (
                                            <div className="rounded-lg border divide-y max-h-64 overflow-y-auto">
                                                {auditRows.map((a) => (
                                                    <div key={a.id} className="px-3 py-2 text-xs space-y-0.5">
                                                        <div className="flex items-center justify-between">
                                                            <span className="font-medium">{a.action}</span>
                                                            <span className="text-muted-foreground">{a.changedAt}</span>
                                                        </div>
                                                        <div className="text-muted-foreground">
                                                            by {a.changedByName || a.changedByEid}
                                                            {a.sigId != null ? ` — sig #${a.sigId}` : ""}
                                                        </div>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </CollapsibleContent>
                                </Collapsible>
                            </div>
                        )}
                    </div>

                    {/* ── Right: PDF preview ── */}
                    <div className="relative flex-1 min-h-0 bg-muted/20">
                        {pdfLoading && (
                            <div className="absolute inset-0 z-10 flex items-center justify-center gap-2 bg-background/60 text-muted-foreground">
                                <Loader2 className="h-5 w-5 animate-spin" />
                                <span className="text-sm">Loading document…</span>
                            </div>
                        )}
                        {pdfError && !pdfLoading && (
                            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-4 text-center">
                                <AlertTriangle className="h-8 w-8 text-destructive" />
                                <p className="text-sm text-muted-foreground">{pdfError}</p>
                                <Button size="sm" variant="outline" onClick={() => setPdfReloadToken((c) => c + 1)}>
                                    Retry
                                </Button>
                            </div>
                        )}
                        {pdfUrl && !pdfError && (
                            <iframe src={pdfUrl} title="Document preview" className="h-full w-full border-0" />
                        )}
                    </div>
                </div>

                <DialogFooter className="shrink-0 border-t px-4 py-3">
                    <Button variant="outline" onClick={onClose} disabled={saving}>
                        Close
                    </Button>
                    <Button onClick={handleSave} disabled={saving || loading} className="gap-1.5">
                        {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                        Save Changes
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    )
}
