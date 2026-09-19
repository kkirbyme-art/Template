/* eslint-disable @typescript-eslint/no-explicit-any */
import {
  useState,
  useEffect,
  useCallback,
  useMemo,
  Fragment,
  useRef,
} from 'react'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@/components/ui/tooltip'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Checkbox } from '@/components/ui/checkbox'
import { ScrollArea } from '@/components/ui/scroll-area'
import {
  ChevronDown,
  ChevronRight,
  FileText,
  Search,
  RefreshCw,
  Clock,
  User,
  Building2,
  X,
  Eye,
  EyeOff,
  FileSignature,
  UserCircle,
  Info,
  PenLine,
  Layers,
  CalendarDays,
  Inbox,
  AlertTriangle,
  Hash,
  ListOrdered,
  CheckCircle2,
  CircleDot,
  Circle,
  Loader2,
  XCircle,
  Menu,
  CalendarClock,
  Users,
  // Upload (unused here)
} from 'lucide-react'
import { pdfjs } from 'react-pdf';
import { baseEID, baseUser_Type, basePathUrl, baseLevel, authFetch, apiControllerBase, baseCP, baseEMAIL, apiUrl } from '@/lib/config'
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { toast } from 'sonner'
import PDFSigningView from '@/View/dgsign/pdf_viewer'
import SignatureVerifyDialog from '@/View/dgsign/signature-verify-dialog'

pdfjs.GlobalWorkerOptions.workerSrc = new URL(
  'pdfjs-dist/build/pdf.worker.min.mjs',
  import.meta.url,
).toString();

// ---------- Types ----------

interface PendingDocumentType {
  id: number
  documentDescription: string
  pendingCount: number
}

interface PendingDocument {
  DocId: number
  DocName: string | null
  DocCode: string | null
  DocDescription: string | null
  DocCreatedDatetime: string | null
  DocCreatedByName: string | null
  DocCreatedByPosition: string | null
  DocCreatedByOffice: string | null
  DocStatusId: number
  DocStatusName: string | null
  DocTypeName: string | null
  DocTypeAbbr: string | null
  SigId: number
  SigOrder: number
  SigEid: number
  SigUserType: number
  SignatoryName: string | null
  SignatoryPosition: string | null
  SignatoryOffice: string | null
  SigStatus: number
  SigStatusName: string | null
  SignatoryAssignedDatetime: string | null
  CurrentSignatureDatetime: string | null
  LastActionDatetime: string | null
  LastActionType: string | null
  LastActionByName: string | null
  LastActionByPosition: string | null
  LastActionByOffice: string | null
  SigLevel: number
  SigSignCount: number
}

interface Signatory {
  sigId: number
  docId: number
  sigCode: string
  sigEid: number
  sigStatus: number
  sigOrder: number
  sigRemarks: string
  sigUserType: number
  sigLevel: number
  sigSignCount: number
  sigRemarksDatenTime: string | null
  dateTimeInserted: string | null
  signStatusId: number
  signStatusDescription: string
  fname: string
  position: string
  officeName: string
  signDatetime: string | null
  receivedDatetime: string | null
  daysPending: number | null
  counterSignedByName: string | null
  counterSignedByPosition: string | null
}

interface GroupedSignatory {
  sigOrder: number
  signatoryCount: number
  signatories: Signatory[]
}

// ---------- Skeleton Row ----------

const SkeletonRow = ({
  minimal,
  hasExpand,
}: {
  minimal: boolean
  hasExpand?: boolean
}) => (
  <TableRow className="animate-pulse">
    <TableCell>
      <div className="h-4 w-4 bg-muted rounded-sm" />
    </TableCell>
    {hasExpand && (
      <TableCell>
        <div className="h-4 w-4 bg-primary/20 rounded-full" />
      </TableCell>
    )}
    <TableCell>
      <div className="h-4 bg-muted rounded-md w-full" />
    </TableCell>
    <TableCell>
      <div className="h-4 bg-muted rounded-md w-20" />
    </TableCell>
    <TableCell>
      <div className="h-4 bg-muted rounded-md w-24" />
    </TableCell>
    {!minimal && (
      <>
        <TableCell>
          <div className="h-4 bg-muted rounded-md w-20" />
        </TableCell>
        <TableCell>
          <div className="h-4 bg-muted rounded-md w-20" />
        </TableCell>
      </>
    )}
    <TableCell>
      <div className="h-4 w-4 bg-muted rounded-full mx-auto" />
    </TableCell>
  </TableRow>
)

const SkeletonCard = () => (
  <div className="animate-pulse border border-border rounded-lg p-3 space-y-2 bg-card">
    <div className="flex items-start gap-3">
      <div className="h-4 w-4 bg-primary/20 rounded mt-0.5 shrink-0" />
      <div className="flex-1 space-y-1.5">
        <div className="h-4 bg-muted rounded w-3/4" />
        <div className="h-3 bg-muted rounded w-1/3" />
        <div className="flex gap-2 mt-1">
          <div className="h-5 bg-muted rounded-full w-16" />
          <div className="h-3 bg-muted rounded w-1/2 mt-1" />
        </div>
      </div>
      <div className="h-5 bg-muted rounded-full w-16 shrink-0" />
    </div>
  </div>
)

// ---------- Helpers ----------

function formatDate(raw: string | null | undefined): string {
  if (!raw) return '—'
  const d = new Date(raw)
  if (isNaN(d.getTime())) return raw
  return d.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function fmtShort(val: string | null): string | null {
  if (!val) return null
  const d = new Date(val)
  if (isNaN(d.getTime())) return null
  return d.toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric',
    hour: 'numeric', minute: '2-digit',
  })
}

function statusBadgeClass(status: string | null): string {
  switch (status?.toLowerCase()) {
    case 'pending':
      return 'bg-yellow-100 text-yellow-800 border-yellow-300 dark:bg-yellow-900/30 dark:text-yellow-500'
    case 'signed':
      return 'bg-green-100 text-green-800 border-green-300 dark:bg-green-900/30 dark:text-green-500'
    case 'returned':
      return 'bg-red-100 text-red-800 border-red-300 dark:bg-red-900/30 dark:text-red-500'
    default:
      return 'bg-gray-100 text-gray-800 border-gray-300 dark:bg-gray-800 dark:text-gray-400'
  }
}

// ---------- Detail Panel Component ----------

// Label/value row used throughout DetailPanel. `min-w-0` on the row and the
// value span is required for `truncate` to actually clip inside a flex
// container — without it, long values (office names, descriptions, etc.)
// overflow past the card instead of ellipsizing.
const DetailField = ({
  icon: Icon,
  label,
  value,
  wrap = false,
}: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  value?: React.ReactNode
  // When true, a long value wraps onto the next line(s) instead of being
  // truncated with an ellipsis on the same line as the label.
  wrap?: boolean
}) => {
  const display = value === null || value === undefined || value === '' ? '—' : value
  const title = typeof display === 'string' || typeof display === 'number' ? String(display) : undefined

  if (wrap) {
    return (
      <div className="min-w-0">
        <div className="flex items-center gap-1.5">
          <Icon className="h-3 w-3 text-muted-foreground shrink-0" />
          <span className="text-muted-foreground">{label}:</span>
        </div>
        <p className="mt-0.5 pl-4.5 wrap-break-word whitespace-pre-wrap">{display}</p>
      </div>
    )
  }

  return (
    <div className="flex items-start gap-1.5 min-w-0">
      <Icon className="h-3 w-3 mt-0.5 text-muted-foreground shrink-0" />
      <span className="text-muted-foreground shrink-0">{label}:</span>
      <span className="truncate min-w-0 flex-1" title={title}>{display}</span>
    </div>
  )
}

const DetailPanel = ({ doc }: { doc: PendingDocument }) => {
  const [groupedSigs, setGroupedSigs] = useState<GroupedSignatory[]>([])
  const [loadingSigs, setLoadingSigs] = useState(false)
  const [showMetadata, setShowMetadata] = useState(false)

  useEffect(() => {
    const fetchSignatories = async () => {
      setLoadingSigs(true)
      try {
        const res = await authFetch(
          `${basePathUrl}api/${apiControllerBase}/get_document_view?docId=${doc.DocId}`,
          { method: 'GET', headers: { 'Content-Type': 'application/json' } },
        )


        if (!res.ok) throw new Error('Failed to fetch signatories')
        const data = await res.json()
        setGroupedSigs(data.grouped ?? [])
      } catch (err) {
        console.error(err)
      } finally {
        setLoadingSigs(false)
      }
    }
    fetchSignatories()
  }, [doc.DocId])

  return (
    <div className="p-5 bg-muted/30 rounded-lg border border-border space-y-5 text-xs">
      {/* Info Cards Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-5">
        {/* Document Info */}
        <div className="space-y-3 bg-card rounded-lg p-4 border border-primary/20 min-w-0">
          <h4 className="font-semibold flex items-center gap-2 text-primary text-sm">
            <div className="p-1.5 rounded-md bg-primary/10">
              <FileText className="h-3.5 w-3.5" />
            </div>
            Document Details
          </h4>
          <div className="space-y-1.5 text-foreground/80">
            <DetailField icon={Hash} label="Reference No." value={doc.DocId} />
            <DetailField icon={FileText} label="Name" value={doc.DocName} />
            <DetailField icon={Info} label="Description" value={doc.DocDescription} wrap />
            <DetailField
              icon={Layers}
              label="Type"
              value={doc.DocTypeName ? `${doc.DocTypeName} (${doc.DocTypeAbbr ?? '—'})` : undefined} wrap
            />
            <div className="flex items-start gap-1.5 min-w-0">
              <Info className="h-3 w-3 mt-0.5 text-muted-foreground shrink-0" />
              <span className="text-muted-foreground shrink-0">Status:</span>
              <Badge
                variant="outline"
                className={`text-xs ${statusBadgeClass(doc.DocStatusName)}`}
              >
                {doc.DocStatusName ?? '—'}
              </Badge>
            </div>
          </div>
        </div>

        {/* Created By */}
        <div className="space-y-3 bg-card rounded-lg p-4 border border-primary/20 min-w-0">
          <h4 className="font-semibold flex items-center gap-2 text-primary text-sm">
            <div className="p-1.5 rounded-md bg-primary/10">
              <UserCircle className="h-3.5 w-3.5" />
            </div>
            Created By
          </h4>
          <div className="space-y-1.5 text-foreground/80">
            <DetailField icon={User} label="Name" value={doc.DocCreatedByName} />
            <DetailField icon={Info} label="Position" value={doc.DocCreatedByPosition} />
            <DetailField icon={Building2} label="Office" value={doc.DocCreatedByOffice} />
            <DetailField icon={CalendarDays} label="Date" value={formatDate(doc.DocCreatedDatetime)} />
          </div>
        </div>

        {/* Signature Info */}
        <div className="space-y-3 bg-card rounded-lg p-4 border border-primary/20 min-w-0">
          <h4 className="font-semibold flex items-center gap-2 text-primary text-sm">
            <div className="p-1.5 rounded-md bg-primary/10">
              <PenLine className="h-3.5 w-3.5" />
            </div>
            Signature Info
          </h4>
          <div className="space-y-1.5 text-foreground/80">
            <DetailField icon={User} label="Signatory" value={doc.SignatoryName} />
            <DetailField icon={Info} label="Position" value={doc.SignatoryPosition} />
            <DetailField icon={Building2} label="Office" value={doc.SignatoryOffice} />
            <DetailField icon={CalendarDays} label="Assigned" value={formatDate(doc.SignatoryAssignedDatetime)} />
            <div className="flex items-start gap-1.5 min-w-0">
              <FileSignature className="h-3 w-3 mt-0.5 text-muted-foreground shrink-0" />
              <span className="text-muted-foreground shrink-0">Status:</span>
              <Badge
                variant="outline"
                className={`text-xs ${statusBadgeClass(doc.SigStatusName)}`}
              >
                {doc.SigStatusName ?? '—'}
              </Badge>
            </div>
          </div>
        </div>

        {/* Last Action */}
        <div className="space-y-3 bg-card rounded-lg p-4 border border-primary/20 min-w-0">
          <h4 className="font-semibold flex items-center gap-2 text-primary text-sm">
            <div className="p-1.5 rounded-md bg-primary/10">
              <Clock className="h-3.5 w-3.5" />
            </div>
            Last Action
          </h4>
          <div className="space-y-1.5 text-foreground/80">
            <DetailField icon={Layers} label="Type" value={doc.LastActionType} />
            <DetailField icon={User} label="By" value={doc.LastActionByName} />
            <DetailField icon={Info} label="Position" value={doc.LastActionByPosition} />
            <DetailField icon={Building2} label="Office" value={doc.LastActionByOffice} />
            <DetailField icon={CalendarDays} label="Date" value={formatDate(doc.LastActionDatetime)} />
          </div>
        </div>

        {/* Additional Metadata — only visible when baseLevel === 1 */}
        {baseLevel === 1 && (
          <div className="space-y-3 bg-card rounded-lg p-4 border border-primary/20 min-w-0">
            <button
              type="button"
              onClick={() => setShowMetadata(!showMetadata)}
              className="font-semibold flex items-center gap-2 text-primary text-sm w-full text-left"
            >
              <div className="p-1.5 rounded-md bg-primary/10">
                <Info className="h-3.5 w-3.5" />
              </div>
              Additional Info
              <ChevronDown
                className={`h-3.5 w-3.5 ml-auto transition-transform duration-200 ${showMetadata ? 'rotate-180' : ''}`}
              />
            </button>
            {showMetadata && (
              <div className="space-y-1.5 text-foreground/80 animate-in fade-in slide-in-from-top-1 duration-200">
                <DetailField icon={Hash} label="Sig ID" value={doc.SigId} />
                <DetailField icon={Hash} label="Sig Level" value={doc.SigLevel} />
                <DetailField icon={Hash} label="Sig Count" value={doc.SigSignCount} />
                <DetailField icon={ListOrdered} label="Sig Order" value={doc.SigOrder} />
                <DetailField icon={Hash} label="Sig EID" value={doc.SigEid} />
                <DetailField icon={User} label="User Type" value={doc.SigUserType} />
                <DetailField
                  icon={PenLine}
                  label="Current Signature"
                  value={formatDate(doc.CurrentSignatureDatetime)}
                />
              </div>
            )}
          </div>
        )}
      </div>

      {/* Approval Progress Timeline */}
      <div className="bg-card rounded-lg p-4 border border-primary/20 min-w-0">
        <h4 className="font-semibold flex items-center gap-2 text-primary mb-4">
          <div className="p-1.5 rounded-md bg-primary/10">
            <FileSignature className="h-3.5 w-3.5" />
          </div>
          <span className="text-sm">Approval Progress Timeline</span>
        </h4>

        {loadingSigs ? (
          <div className="flex items-center justify-center py-6">
            <Loader2 className="h-5 w-5 animate-spin text-primary" />
            <span className="ml-2 text-sm text-muted-foreground">
              Loading signatories...
            </span>
          </div>
        ) : groupedSigs.length === 0 ? (
          <p className="text-sm text-muted-foreground text-center py-4">
            No signatory data available
          </p>
        ) : (
          <div className="relative">
            {groupedSigs.map((group, groupIdx) => {
              const allSigned = group.signatories.every(
                (s) => s.signStatusId === 1,
              )
              const someSigned = group.signatories.some(
                (s) => s.signStatusId === 1,
              )
              const isLast = groupIdx === groupedSigs.length - 1

              return (
                <div key={group.sigOrder} className="relative flex gap-4">
                  {/* Timeline connector + status icon */}
                  <div className="flex flex-col items-center">
                    <div
                      className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full border-2 ${allSigned
                        ? 'bg-green-100 border-green-500 dark:bg-green-900/30 dark:border-green-400'
                        : someSigned
                          ? 'bg-yellow-100 border-yellow-500 dark:bg-yellow-900/30 dark:border-yellow-400'
                          : 'bg-muted border-muted-foreground/30'
                        }`}
                    >
                      {allSigned ? (
                        <CheckCircle2 className="h-4 w-4 text-green-600 dark:text-green-400" />
                      ) : someSigned ? (
                        <CircleDot className="h-4 w-4 text-yellow-600 dark:text-yellow-400" />
                      ) : (
                        <Circle className="h-4 w-4 text-muted-foreground" />
                      )}
                    </div>
                    {!isLast && (
                      <div
                        className={`w-0.5 flex-1 min-h-5 ${allSigned
                          ? 'bg-green-400 dark:bg-green-600'
                          : 'bg-muted-foreground/20'
                          }`}
                      />
                    )}
                  </div>

                  {/* Step content */}
                  <div className={`pb-6 flex-1 ${isLast ? 'pb-0' : ''}`}>
                    <div className="flex items-center gap-2 mb-2">
                      <span className="font-medium text-xs">
                        Step {groupIdx + 1}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        (Order {group.sigOrder})
                      </span>
                      <Badge
                        variant="outline"
                        className={`text-xs ${allSigned
                          ? 'bg-green-100 text-green-800 border-green-300 dark:bg-green-900/30 dark:text-green-500'
                          : 'bg-yellow-100 text-yellow-800 border-yellow-300 dark:bg-yellow-900/30 dark:text-yellow-500'
                          }`}
                      >
                        {allSigned
                          ? 'Completed'
                          : `${group.signatories.filter((s) => s.signStatusId === 1).length}/${group.signatoryCount} Signed`}
                      </Badge>
                    </div>
                    <div className="space-y-2">
                      {group.signatories.map((sig) => (
                        <div
                          key={sig.sigId}
                          className="flex items-start gap-2 p-2.5 rounded-md bg-muted/50 border border-border/50"
                        >
                          {sig.signStatusId === 1 ? (
                            <CheckCircle2 className="h-4 w-4 mt-0.5 text-green-600 dark:text-green-400 shrink-0" />
                          ) : sig.signStatusDescription
                            ?.trim()
                            .toLowerCase() === 'returned' ? (
                            <XCircle className="h-4 w-4 mt-0.5 text-red-600 dark:text-red-400 shrink-0" />
                          ) : (
                            <Clock className="h-4 w-4 mt-0.5 text-yellow-600 dark:text-yellow-400 shrink-0" />
                          )}
                          <div className="flex-1 min-w-0">
                            <div className="font-medium text-xs truncate">
                              {sig.fname?.trim()}
                            </div>
                            <div className="text-xs text-muted-foreground truncate">
                              {sig.position?.trim()}
                            </div>
                            <div className="text-xs text-muted-foreground truncate">
                              {sig.officeName?.trim()}
                            </div>

                            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 mt-1.5">
                              {fmtShort(sig.receivedDatetime) && (
                                <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
                                  <CalendarClock className="h-3 w-3" />
                                  Received {fmtShort(sig.receivedDatetime)}
                                  {sig.daysPending !== null &&
                                    (sig.signStatusId === 1
                                      ? ` (took ${sig.daysPending}d to sign)`
                                      : ` (${sig.daysPending}d ago)`)}
                                </span>
                              )}
                              {sig.signStatusId === 1 && fmtShort(sig.signDatetime) && (
                                <span className="inline-flex items-center gap-1 text-[11px] text-green-700 dark:text-green-400">
                                  <CheckCircle2 className="h-3 w-3" />
                                  Signed {fmtShort(sig.signDatetime)}
                                </span>
                              )}
                              {sig.counterSignedByName && (
                                <span className="inline-flex items-center gap-1 text-[11px] text-blue-700 dark:text-blue-400">
                                  <Users className="h-3 w-3" />
                                  Countersigned by {sig.counterSignedByName.trim()}
                                  {sig.counterSignedByPosition && ` (${sig.counterSignedByPosition.trim()})`}
                                </span>
                              )}
                            </div>
                          </div>
                          <Badge
                            variant="outline"
                            className={`text-xs shrink-0 ${statusBadgeClass(sig.signStatusDescription?.trim())}`}
                          >
                            {sig.signStatusDescription?.trim() ?? '—'}
                          </Badge>
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}

// ---------- Component ----------

interface AlternatePrincipal {
  principalEid: number
  principalUserType: number
  principalName: string | null
  principalOffice: string | null
}

export default function ForSignatureAlternateQueueView() {
  const currentYear = new Date().getFullYear()

  const [principals, setPrincipals] = useState<AlternatePrincipal[]>([])
  const [selectedPrincipal, setSelectedPrincipal] = useState<AlternatePrincipal | null>(null)
  const [loadingPrincipals, setLoadingPrincipals] = useState(true)

  useEffect(() => {
    let cancelled = false
    authFetch(apiUrl('AlternateSignatories/my_principals'))
      .then((res) => (res.ok ? res.json() : []))
      .then((data: AlternatePrincipal[]) => {
        if (cancelled) return
        setPrincipals(data)
        setSelectedPrincipal((prev) => prev ?? data[0] ?? null)
      })
      .catch(() => { if (!cancelled) setPrincipals([]) })
      .finally(() => { if (!cancelled) setLoadingPrincipals(false) })
    return () => { cancelled = true }
  }, [])

  const [year, setYear] = useState<number>(currentYear)
  const [docTypes, setDocTypes] = useState<PendingDocumentType[]>([])
  const [documents, setDocuments] = useState<PendingDocument[]>([])
  const [activeTab, setActiveTab] = useState<string>('')
  const [searchQuery, setSearchQuery] = useState('')
  const [loadingTypes, setLoadingTypes] = useState(false)
  const [loadingDocs, setLoadingDocs] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [minimalView, setMinimalView] = useState(false) // true = minimal, false = detailed
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set())
  const [checkedDocs, setCheckedDocs] = useState<Set<string>>(new Set())
  const [batchSignOpen, setBatchSignOpen] = useState(false)
  const [verifyDialogOpen, setVerifyDialogOpen] = useState(false)
  const [verifyHasPfx, setVerifyHasPfx] = useState<boolean>(false)
  const [verifyChecking, setVerifyChecking] = useState<boolean>(false)
  const [preVerified, setPreVerified] = useState<{ method: 'pincode' | 'password'; value: string } | null>(null)
  const [batchActiveDocKey, setBatchActiveDocKey] = useState<string>('')
  const [batchSidebarOpen, setBatchSidebarOpen] = useState(false) // new state for mobile sidebar
  const pdfContainerRef = useRef<HTMLDivElement>(null)
  const [bulkWarningOpen, setBulkWarningOpen] = useState(false)
  const [bulkSignMode, setBulkSignMode] = useState(false)
  const [bulkProcessing, setBulkProcessing] = useState(false)
  const signingModeRef = useRef<'batch' | 'bulk'>('batch')
  const [batchSignedDocs, setBatchSignedDocs] = useState<Set<string>>(new Set())
  const [bulkDocStatuses, setBulkDocStatuses] = useState<Record<string, { status: 'pending' | 'signing' | 'signed' | 'failed'; reason?: string }>>({})
  const preVerifiedRef = useRef<{ method: 'pincode' | 'password'; value: string } | null>(null)
  const bulkProcessingRef = useRef(false)

  // Fetch document types
  const fetchDocTypes = useCallback(async () => {
    if (!selectedPrincipal) { setDocTypes([]); setActiveTab(''); setDocuments([]); return }
    setLoadingTypes(true)
    setError(null)
    try {
      const params = new URLSearchParams({
        year: String(year),
        principalEid: String(selectedPrincipal.principalEid),
        principalUserType: String(selectedPrincipal.principalUserType),
      })
      const res = await authFetch(
        apiUrl(`AlternateSignatories/pending_document_types_as_alternate?${params}`),
        { method: 'GET', headers: { 'Content-Type': 'application/json' } },
      )
      if (!res.ok) throw new Error('Failed to fetch document types')
      const data: PendingDocumentType[] = await res.json()
      setDocTypes(data)
      if (data.length > 0) {
        setActiveTab(String(data[0].id))
      } else {
        setActiveTab('')
        setDocuments([])
      }
    } catch (err) {
      console.error(err)
      setError('Failed to load document types')
    } finally {
      setLoadingTypes(false)
    }
  }, [year, selectedPrincipal])

  // Fetch pending documents — map API fields (lowercase) to our interface (uppercase)
  const fetchDocuments = useCallback(
    async (docTypeId: string) => {
      if (!docTypeId || !selectedPrincipal) return
      setLoadingDocs(true)
      setError(null)
      try {
        const params = new URLSearchParams({
          year: String(year),
          principalEid: String(selectedPrincipal.principalEid),
          principalUserType: String(selectedPrincipal.principalUserType),
          docTypeId,
        })
        const res = await authFetch(
          apiUrl(`AlternateSignatories/pending_documents_as_alternate?${params}`),
          { method: 'GET', headers: { 'Content-Type': 'application/json' } },
        )
        console.log(res);
        if (!res.ok) throw new Error('Failed to fetch pending documents')
        const data: any[] = await res.json()
        // Map snake_case / lowercase to our PascalCase interface
        const mapped: PendingDocument[] = data.map((item) => ({
          DocId: item.docId,
          DocName: item.docName,
          DocCode: item.docCode,
          DocDescription: item.docDescription,
          DocCreatedDatetime: item.docCreatedDatetime,
          DocCreatedByName: item.docCreatedByName,
          DocCreatedByPosition: item.docCreatedByPosition,
          DocCreatedByOffice: item.docCreatedByOffice,
          DocStatusId: item.docStatusId,
          DocStatusName: item.docStatusName,
          DocTypeName: item.docTypeName,
          DocTypeAbbr: item.docTypeAbbr,
          SigId: item.sigId,
          SigOrder: item.sigOrder,
          SigEid: item.sigEid,
          SigUserType: item.sigUserType,
          SignatoryName: item.signatoryName,
          SignatoryPosition: item.signatoryPosition,
          SignatoryOffice: item.signatoryOffice,
          SigStatus: item.sigStatus,
          SigStatusName: item.sigStatusName,
          SignatoryAssignedDatetime: item.signatoryAssignedDatetime,
          CurrentSignatureDatetime: item.currentSignatureDatetime,
          LastActionDatetime: item.lastActionDatetime,
          LastActionType: item.lastActionType,
          LastActionByName: item.lastActionByName,
          LastActionByPosition: item.lastActionByPosition,
          LastActionByOffice: item.lastActionByOffice,
          SigLevel: item.sigLevel,
          SigSignCount: item.sigSignCount,
        }))
        setDocuments(mapped)
      } catch (err) {
        console.error(err)
        setError('Failed to load pending documents')
      } finally {
        setLoadingDocs(false)
      }
    },
    [year, selectedPrincipal],
  )

  useEffect(() => {
    fetchDocTypes()
  }, [fetchDocTypes])

  useEffect(() => {
    if (activeTab) {
      fetchDocuments(activeTab)
    }
  }, [activeTab, fetchDocuments])

  // Filter by search query
  const filtered = useMemo(() => {
    if (!searchQuery) return documents
    const q = searchQuery.toLowerCase()
    return documents.filter(
      (doc) =>
        doc.DocName?.toLowerCase().includes(q) ||
        doc.DocCode?.toLowerCase().includes(q) ||
        doc.DocDescription?.toLowerCase().includes(q) ||
        doc.DocCreatedByName?.toLowerCase().includes(q) ||
        doc.DocCreatedByOffice?.toLowerCase().includes(q) ||
        doc.DocTypeName?.toLowerCase().includes(q),
    )
  }, [documents, searchQuery])

  // Year options (last 5 years)
  const yearOptions = Array.from({ length: 5 }, (_, i) => currentYear - i)

  // Clear search
  const clearSearch = () => setSearchQuery('')

  // Toggle row expansion
  const toggleRow = (rowKey: string) => {
    setExpandedRows((prev) => {
      const newSet = new Set(prev)
      if (newSet.has(rowKey)) {
        newSet.delete(rowKey)
      } else {
        newSet.add(rowKey)
      }
      return newSet
    })
  }

  // Helper to get unique row key
  const getRowKey = (doc: PendingDocument) => `${doc.DocId}-${doc.SigId}`

  // View PDF (read-only) — opens the document in its own popup window (not a
  // browser tab) without entering the sign flow. Passing sizing/position
  // features in the `window.open` features string is what makes browsers
  // render a standalone window instead of a new tab.
  const openPdfInWindow = (url: string, docId: number) => {
    const width = Math.min(1000, window.screen.availWidth - 100)
    const height = Math.min(900, window.screen.availHeight - 100)
    const left = Math.max(0, Math.round((window.screen.availWidth - width) / 2))
    const top = Math.max(0, Math.round((window.screen.availHeight - height) / 2))
    const features = `popup=yes,noopener,noreferrer,width=${width},height=${height},left=${left},top=${top},resizable=yes,scrollbars=yes,toolbar=no,menubar=no,location=no,status=no`
    window.open(url, `pdfview_${docId}`, features)
  }

  const [viewingPdfKey, setViewingPdfKey] = useState<string | null>(null)
  const handleViewPdf = async (doc: PendingDocument, e?: React.MouseEvent) => {
    e?.stopPropagation()
    const rowKey = getRowKey(doc)
    setViewingPdfKey(rowKey)
    try {
      const res = await authFetch(`${basePathUrl}api/${apiControllerBase}/get_pdf_digital_only?formId=${doc.DocId}&isDownload=0`)
      if (!res.ok) throw new Error('Failed to load PDF')
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      openPdfInWindow(url, doc.DocId)
      setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } catch (err) {
      console.error(err)
      toast.error('Failed to open PDF')
    } finally {
      setViewingPdfKey(null)
    }
  }

  // Checkbox helpers
  const toggleDocCheck = (rowKey: string) => {
    setCheckedDocs((prev) => {
      const newSet = new Set(prev)
      if (newSet.has(rowKey)) {
        newSet.delete(rowKey)
      } else {
        newSet.add(rowKey)
      }
      return newSet
    })
  }

  const allFilteredChecked =
    filtered.length > 0 &&
    filtered.every((doc) => checkedDocs.has(getRowKey(doc)))

  const toggleAllChecked = () => {
    if (allFilteredChecked) {
      setCheckedDocs(new Set())
    } else {
      setCheckedDocs(new Set(filtered.map((doc) => getRowKey(doc))))
    }
  }

  const checkedDocsList = useMemo(
    () => filtered.filter((doc) => checkedDocs.has(getRowKey(doc))),
    [filtered, checkedDocs],
  )

  // Batch sign handlers
  // Previously used to open batch sign UI; kept removed to avoid unused warning

  // Prepare sign: check if a stored PFX password exists then show verification dialog
  const prepareSign = async (mode: 'batch' | 'bulk' = 'batch') => {
    signingModeRef.current = mode
    if (checkedDocsList.length === 0) return
    setVerifyChecking(true)
    try {
      const res = await authFetch(`${basePathUrl}api/${apiControllerBase}/get_pfx_attachments_by_eid?eid=${baseEID}&userType=${baseUser_Type}`)
      if (!res.ok) throw new Error('Failed to check certificate status')
      const hasPassword: boolean = await res.json()
      setVerifyHasPfx(hasPassword)
      setVerifyDialogOpen(true)
    } catch (err) {
      console.error('prepareSign error', err)
    } finally {
      setVerifyChecking(false)
    }
  }

  const handleVerified = async (data: { method: 'sms' | 'email' | 'pincode'; pin: string } | { method: 'password'; password: string }) => {
    setVerifyChecking(true)
    try {
      if ('password' in data) {
        // verify password via API
        const res = await authFetch(`${basePathUrl}api/${apiControllerBase}/get_checkpassword_by_eid?eid=${baseEID}&userType=${baseUser_Type}&password=${encodeURIComponent(data.password)}`)
        if (!res.ok) throw new Error('Failed to verify password')
        const ok: boolean = await res.json()
        if (!ok) {
          toast.error('Invalid certificate password. Please try again.')
          return false
        }
        setPreVerified({ method: 'password', value: data.password })
        preVerifiedRef.current = { method: 'password', value: data.password }
      } else {
        // verify pin via API
        const res = await authFetch(`${basePathUrl}api/${apiControllerBase}/get_pincode_by_eid?eid=${baseEID}&userType=${baseUser_Type}&pincode=${encodeURIComponent(data.pin)}`)
        if (!res.ok) throw new Error('Failed to verify pin')
        const ok: boolean = await res.json()
        if (!ok) {
          toast.error('Invalid PIN code. Please try again.')
          return false
        }
        setPreVerified({ method: 'pincode', value: data.pin })
        preVerifiedRef.current = { method: 'pincode', value: data.pin }
      }

      // verification succeeded — close dialog and open batch sign
      setVerifyDialogOpen(false)
      setBatchActiveDocKey(getRowKey(checkedDocsList[0]))
      setBatchSidebarOpen(false)
      if (signingModeRef.current === 'bulk') {
        setBulkSignMode(true)
      }
      setBatchSignOpen(true)
      return true
    } catch (err) {
      console.error('handleVerified error', err)
      toast.error('Verification failed')
      return false
    } finally {
      setVerifyChecking(false)
    }
  }

  const batchActiveDoc = useMemo(
    () => checkedDocsList.find((d) => getRowKey(d) === batchActiveDocKey),
    [checkedDocsList, batchActiveDocKey],
  )

  const handleBatchDocSigned = () => {
    const signedKey = batchActiveDocKey
    const updatedSigned = new Set(batchSignedDocs).add(signedKey)
    setBatchSignedDocs(updatedSigned)

    // More than one document was checked — advance to the next unsigned
    // one in that queue. (A single checked document has nothing to
    // advance to; PDFSigningView reloads the signed PDF in place instead —
    // see the `reloadAfterSave` prop below.)
    if (checkedDocsList.length > 1) {
      const currentIdx = checkedDocsList.findIndex(d => getRowKey(d) === signedKey)
      const next = checkedDocsList.find((d, idx) => idx > currentIdx && !updatedSigned.has(getRowKey(d)))
      if (next) {
        const nextKey = getRowKey(next)
        setTimeout(() => setBatchActiveDocKey(nextKey), 400)
      }
    }
  }

  const handleBulkSignClick = () => {
    if (checkedDocsList.length <= 1) return
    setBulkWarningOpen(true)
  }

  const handleBulkWarningConfirm = () => {
    setBulkWarningOpen(false)
    prepareSign('bulk')
  }

  const handleBulkFirstDocSigned = async (location: {
    page: number; xPct: number; yPct: number; isSpecimen: boolean; authorityLevel: number
  }) => {
    setBulkSignMode(false)
    setBulkProcessing(true)
    bulkProcessingRef.current = true

    const snapshot = [...checkedDocsList] // capture list before any state changes
    const firstDocKey = getRowKey(snapshot[0])
    const remainingDocs = snapshot.slice(1)

    // Mark first doc as signed; all others start as pending
    const initialStatuses: Record<string, { status: 'pending' | 'signing' | 'signed' | 'failed'; reason?: string }> = {
      [firstDocKey]: { status: 'signed' },
    }
    for (const doc of remainingDocs) {
      initialStatuses[getRowKey(doc)] = { status: 'pending' }
    }
    setBulkDocStatuses(initialStatuses)
    setBatchSignedDocs(prev => new Set([...prev, firstDocKey]))

    let latitude = ''
    let longitude = ''
    let accuracy = ''
    try {
      const pos = await new Promise<GeolocationPosition>((resolve, reject) =>
        navigator.geolocation.getCurrentPosition(resolve, reject, { timeout: 5000 })
      )
      latitude = String(pos.coords.latitude)
      longitude = String(pos.coords.longitude)
      accuracy = String(pos.coords.accuracy)
    } catch { /* ignore */ }

    const deviceType = /Mobi|Android/i.test(navigator.userAgent) ? 'Mobile' : 'Desktop'
    const creds = preVerifiedRef.current

    let signedCount = 1
    let failedCount = 0

    for (const doc of remainingDocs) {
      const docKey = getRowKey(doc)

      // Mark as currently signing
      setBulkDocStatuses(prev => ({ ...prev, [docKey]: { status: 'signing' } }))

      try {
        const pdfRes = await authFetch(
          `${basePathUrl}api/${apiControllerBase}/get_pdf_digital_only?formId=${doc.DocId}&isDownload=0`
        )
        if (!pdfRes.ok) {
          failedCount++
          setBulkDocStatuses(prev => ({ ...prev, [docKey]: { status: 'failed', reason: 'Could not load document PDF' } }))
          continue
        }
        const blob = await pdfRes.blob()
        const arrayBuffer = await blob.arrayBuffer()
        const pdfDoc = await pdfjs.getDocument({ data: arrayBuffer }).promise
        const numPages = pdfDoc.numPages
        await pdfDoc.destroy()

        if (location.page > numPages) {
          failedCount++
          setBulkDocStatuses(prev => ({
            ...prev,
            [docKey]: {
              status: 'failed',
              reason: `Page ${location.page} not found (document has ${numPages} page${numPages === 1 ? '' : 's'})`,
            },
          }))
          continue
        }

        const payload = {
          docId: String(doc.DocId),
          eid: String(baseEID),
          userType: String(baseUser_Type),
          signatures: [{
            page: location.page,
            xPct: location.xPct,
            yPct: location.yPct,
            specimenType: location.isSpecimen ? 1 : 0,
            authorityLevel: location.authorityLevel,
          }],
          bulkPasswords: creds?.method === 'password' ? creds.value : '',
          PinCode: creds?.method === 'pincode' ? creds.value : '',
          bulkDeviceType: deviceType,
          modsId: '1',
          isDisplayDate: 0,
          isDelegate: 0,
          isAlternate: selectedPrincipal ? 1 : 0,
          vwEids: selectedPrincipal ? String(selectedPrincipal.principalEid) : '',
          vwUserType: selectedPrincipal ? String(selectedPrincipal.principalUserType) : '',
          dateNTimeClick: new Date().toISOString(),
          dgLatitude: latitude,
          dgLongitude: longitude,
          dgAccuracy: accuracy,
          dgDeviceType: deviceType,
          dgAddress: '',
        }

        const saveRes = await authFetch(`${basePathUrl}api/${apiControllerBase}/save_signature_image`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
        const resData = await saveRes.json()
        // TEMP DEBUG: server sends the real exception text here (Development only,
        // see SigningService.SaveSignatureImageAsync's catch block).
        if (resData?.debugError) {
          console.error('save_signature_image server error:', resData.debugError)
        }

        if (!saveRes.ok || !resData.success) {
          failedCount++
          setBulkDocStatuses(prev => ({
            ...prev,
            [docKey]: { status: 'failed', reason: resData.message || 'Failed to save signature' },
          }))
        } else {
          signedCount++
          setBulkDocStatuses(prev => ({ ...prev, [docKey]: { status: 'signed' } }))
          setBatchSignedDocs(prev => new Set([...prev, docKey]))
        }
      } catch {
        failedCount++
        setBulkDocStatuses(prev => ({ ...prev, [docKey]: { status: 'failed', reason: 'An unexpected error occurred' } }))
      }
    }

    setBulkProcessing(false)
    bulkProcessingRef.current = false
    setPreVerified(null)
    preVerifiedRef.current = null

    if (failedCount === 0) {
      toast.success(`All ${snapshot.length} documents signed successfully!`)
    } else {
      toast.warning(`${signedCount} signed, ${failedCount} failed — check the list for details.`)
    }
  }

  // Determine number of columns for colspan (updated with min-width approach, but colspan remains same)
  const getColumnCount = () => {
    let count = 3 + (minimalView ? 0 : 2) // Description, Status, Created By, plus optional Office, Last Action
    count += 2 // Expand column + checkbox column
    count += 1 // View column
    return count
  }

  const activeDocType = docTypes.find((dt) => String(dt.id) === activeTab)
  const totalPendingAllTypes = docTypes.reduce((sum, dt) => sum + dt.pendingCount, 0)

  return (
    <div className="w-full min-w-0 space-y-6">
      <div className="w-full min-w-0 flex flex-col gap-4 p-4 md:p-6">
        {/* Principal picker — at the very top, since one alternate can be
            chosen by several principals at once (e.g. both employee 1 and
            employee 2 set employee 3 as their alternate). */}
        <div className="flex items-center gap-2 rounded-xl border bg-card p-3">
          <Users className="h-4 w-4 text-muted-foreground shrink-0" />
          <span className="text-sm text-muted-foreground shrink-0">Signing on behalf of:</span>
          <Select
            value={selectedPrincipal ? `${selectedPrincipal.principalEid}-${selectedPrincipal.principalUserType}` : ''}
            onValueChange={(v) => {
              const found = principals.find((p) => `${p.principalEid}-${p.principalUserType}` === v)
              setSelectedPrincipal(found ?? null)
            }}
            disabled={loadingPrincipals || principals.length === 0}
          >
            <SelectTrigger className="w-64">
              <SelectValue placeholder={loadingPrincipals ? 'Loading…' : 'Select a principal'} />
            </SelectTrigger>
            <SelectContent>
              {principals.map((p) => (
                <SelectItem key={`${p.principalEid}-${p.principalUserType}`} value={`${p.principalEid}-${p.principalUserType}`}>
                  {p.principalName ?? `EID ${p.principalEid}`}{p.principalOffice ? ` — ${p.principalOffice}` : ''}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {/* Header */}
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 rounded-xl bg-primary p-5 text-primary-foreground shadow-lg">
          <div className="flex items-center gap-3">
            <div className="flex h-12 w-12 items-center justify-center rounded-xl bg-primary-foreground/20 backdrop-blur-sm">
              <PenLine className="h-6 w-6" />
            </div>
            <div>
              <h1 className="text-2xl font-bold tracking-tight">
                For Signature (Alternate)
              </h1>
              <p className="text-sm text-primary-foreground/70">
                Review and sign documents on behalf of your principal
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Select
              value={String(year)}
              onValueChange={(v) => setYear(Number(v))}
            >
              <SelectTrigger className="w-30 border-primary-foreground/30 bg-primary-foreground/15 text-primary-foreground backdrop-blur-sm hover:bg-primary-foreground/25 [&>svg]:text-primary-foreground">
                <CalendarDays className="h-4 w-4 mr-1.5" />
                <SelectValue placeholder="Year" />
              </SelectTrigger>
              <SelectContent>
                {yearOptions.map((y) => (
                  <SelectItem key={y} value={String(y)}>
                    {y}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button
              variant="outline"
              size="icon"
              onClick={fetchDocTypes}
              title="Refresh data"
              className="border-primary-foreground/30 bg-primary-foreground/15 text-primary-foreground backdrop-blur-sm hover:bg-primary-foreground/25 hover:text-primary-foreground"
            >
              <RefreshCw className="h-4 w-4" />
            </Button>
          </div>
        </div>

        {/* Stats cards */}
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <Card className="border-primary/20 bg-primary/5 shadow-sm hover:shadow-md transition-shadow">
            <CardHeader className="flex flex-row items-center justify-between pb-2">
              <CardTitle className="text-sm font-medium text-card-foreground">
                Awaiting Your Signature
              </CardTitle>
              <div className="p-2 rounded-lg bg-primary/10">
                <FileSignature className="h-4 w-4 text-primary" />
              </div>
            </CardHeader>
            <CardContent>
              <div className="text-3xl font-bold text-foreground">
                {documents.length}
              </div>
              <p className="text-xs text-muted-foreground mt-1 truncate">
                {activeDocType ? (
                  <>pending in <span className="font-medium text-foreground">{activeDocType.documentDescription}</span></>
                ) : (
                  'documents pending'
                )}
              </p>
              {totalPendingAllTypes > documents.length && (
                <p className="text-[11px] text-muted-foreground/70 mt-0.5">
                  {totalPendingAllTypes} total across all document types
                </p>
              )}
            </CardContent>
          </Card>
          <Card className="border-secondary/40 bg-secondary/30 shadow-sm hover:shadow-md transition-shadow">
            <CardHeader className="flex flex-row items-center justify-between pb-2">
              <CardTitle className="text-sm font-medium text-card-foreground">
                Document Types
              </CardTitle>
              <div className="p-2 rounded-lg bg-secondary">
                <Layers className="h-4 w-4 text-secondary-foreground" />
              </div>
            </CardHeader>
            <CardContent>
              <div className="text-3xl font-bold text-foreground">
                {docTypes.length}
              </div>
              <p className="text-xs text-muted-foreground mt-1">
                categories available
              </p>
            </CardContent>
          </Card>
          <Card className="border-accent/40 bg-accent/30 shadow-sm hover:shadow-md transition-shadow">
            <CardHeader className="flex flex-row items-center justify-between pb-2">
              <CardTitle className="text-sm font-medium text-card-foreground">
                Selected Year
              </CardTitle>
              <div className="p-2 rounded-lg bg-accent">
                <CalendarDays className="h-4 w-4 text-accent-foreground" />
              </div>
            </CardHeader>
            <CardContent>
              <div className="text-3xl font-bold text-foreground">{year}</div>
              <p className="text-xs text-muted-foreground mt-1">fiscal year</p>
            </CardContent>
          </Card>
        </div>

        {/* Tabs + Table */}
        <Card className="w-full min-w-0 shadow-sm border-border">
          <CardContent className="pt-6">
            <Tabs value={activeTab} onValueChange={setActiveTab}>
              <div className="flex flex-col lg:flex-row lg:items-center justify-between mb-4 gap-3 min-w-0">
                <div className="min-w-0">
                  {!loadingTypes && docTypes.length > 0 && (
                    <p className="text-xs text-muted-foreground mb-1.5 flex items-center gap-1.5">
                      <Layers className="h-3 w-3" />
                      {docTypes.length} document type{docTypes.length === 1 ? '' : 's'} have documents waiting for your signature
                    </p>
                  )}
                  <div className="overflow-x-auto pb-1 -mb-1">
                    <TabsList className="flex-nowrap w-max h-auto gap-1.5 bg-transparent p-0">
                      {docTypes.map((dt) => {
                        const isActive = activeTab === String(dt.id)
                        return (
                          <TabsTrigger
                            key={dt.id}
                            value={String(dt.id)}
                            className="relative whitespace-nowrap gap-2 rounded-lg border border-border data-active:border-primary data-active:shadow-sm"
                          >
                            <Layers className={`h-3.5 w-3.5 ${isActive ? 'text-primary' : 'text-muted-foreground'}`} />
                            <span>{dt.documentDescription}</span>
                            <Badge
                              variant={isActive ? 'default' : 'secondary'}
                              className="ml-0.5 h-5 min-w-5 justify-center px-1.5 text-[11px] tabular-nums"
                            >
                              {dt.pendingCount}
                            </Badge>
                          </TabsTrigger>
                        )
                      })}
                    </TabsList>
                  </div>
                </div>
                <div className="flex items-center gap-2 w-full lg:w-auto shrink-0">
                  <div className="relative flex-1 lg:flex-none">
                    <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                    <Input
                      placeholder="Search documents..."
                      className="pl-8 pr-8 w-full lg:w-75"
                      value={searchQuery}
                      onChange={(e) => setSearchQuery(e.target.value)}
                    />
                    {searchQuery && (
                      <Button
                        variant="ghost"
                        size="icon"
                        className="absolute right-1 top-1 h-7 w-7"
                        onClick={clearSearch}
                      >
                        <X className="h-4 w-4" />
                      </Button>
                    )}
                  </div>
                  <Button
                    variant="outline"
                    size="icon"
                    onClick={() => setMinimalView(!minimalView)}
                    title={minimalView ? 'Switch to detailed view' : 'Switch to minimal view'}
                  >
                    {minimalView ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                  </Button>
                </div>
              </div>

              {/* Batch & Bulk action buttons */}
              <div className="flex items-center gap-2 mb-4 flex-wrap">
                <Button
                  onClick={() => prepareSign('batch')}
                  disabled={checkedDocsList.length === 0 || verifyChecking}
                  className="bg-primary hover:bg-primary/90 text-primary-foreground flex-1 sm:flex-none"
                >
                  {verifyChecking ? <Loader2 className="h-4 w-4 mr-1.5 animate-spin" /> : <FileSignature className="h-4 w-4 mr-1.5" />}
                  <span className="sm:hidden">{checkedDocsList.length <= 1 ? 'Sign' : `Sign (${checkedDocsList.length})`}</span>
                  <span className="hidden sm:inline">{checkedDocsList.length <= 1 ? 'Sign' : `Batch Sign (${checkedDocsList.length})`}</span>
                </Button>
                <Button
                  onClick={handleBulkSignClick}
                  disabled={checkedDocsList.length <= 1 || verifyChecking}
                  className="bg-amber-500 hover:bg-amber-600 text-white border-0 flex-1 sm:flex-none"
                >
                  {verifyChecking ? <Loader2 className="h-4 w-4 mr-1.5 animate-spin" /> : <Layers className="h-4 w-4 mr-1.5" />}
                  Bulk Sign
                </Button>
              </div>

              <TabsContent value={activeTab} className="mt-0 min-w-0">
                {error ? (
                  <div className="flex flex-col items-center gap-3 py-12">
                    <div className="p-3 rounded-full bg-destructive/10">
                      <AlertTriangle className="h-6 w-6 text-destructive" />
                    </div>
                    <div className="text-center">
                      <p className="font-medium text-destructive">{error}</p>
                      <p className="text-sm text-muted-foreground mt-1">
                        Please try refreshing the page
                      </p>
                    </div>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={fetchDocTypes}
                      className="mt-2 border-destructive/30 text-destructive hover:bg-destructive/10"
                    >
                      <RefreshCw className="h-3.5 w-3.5 mr-1.5" /> Try Again
                    </Button>
                  </div>
                ) : (
                  <>
                    {/* Mobile card view */}
                    <div className="md:hidden space-y-2">
                      {loadingDocs || loadingTypes ? (
                        Array.from({ length: 5 }).map((_, i) => (
                          <SkeletonCard key={i} />
                        ))
                      ) : filtered.length === 0 ? (
                        <div className="flex flex-col items-center gap-3 py-16">
                          <div className="p-4 rounded-full bg-muted">
                            <Inbox className="h-8 w-8 text-muted-foreground" />
                          </div>
                          <div className="text-center">
                            <p className="font-medium text-muted-foreground">No documents found</p>
                            <p className="text-sm text-muted-foreground/70 mt-1">
                              {searchQuery ? 'Try adjusting your search terms' : 'There are no pending documents for this category'}
                            </p>
                          </div>
                        </div>
                      ) : (
                        filtered.map((doc) => {
                          const rowKey = getRowKey(doc)
                          const isExpanded = expandedRows.has(rowKey)
                          return (
                            <div key={rowKey} className="border border-border rounded-lg bg-card overflow-hidden">
                              <div className="flex items-start gap-3 p-3">
                                <Checkbox
                                  checked={checkedDocs.has(rowKey)}
                                  onCheckedChange={() => toggleDocCheck(rowKey)}
                                  aria-label={`Select ${doc.DocName}`}
                                  className="mt-0.5 shrink-0"
                                />
                                <div className="flex-1 min-w-0">
                                  <div className="flex items-start justify-between gap-2">
                                    <div className="min-w-0 flex-1">
                                      <div className="font-medium text-sm leading-snug line-clamp-2">
                                        {doc.DocName ?? 'Untitled'}
                                      </div>
                                      <span className="inline-block font-mono text-xs px-1.5 py-0.5 rounded bg-muted text-muted-foreground mt-0.5">
                                        {doc.DocCode ?? '—'}
                                      </span>
                                    </div>
                                    <Badge
                                      variant="outline"
                                      className={`shrink-0 max-w-32 truncate ${statusBadgeClass(doc.DocStatusName)}`}
                                    >
                                      {doc.DocStatusName ?? '—'}
                                    </Badge>
                                  </div>
                                  <div className="flex items-center gap-1.5 mt-1.5 text-xs text-muted-foreground truncate">
                                    <User className="h-3 w-3 shrink-0" />
                                    <span className="truncate">{doc.DocCreatedByName ?? '—'}</span>
                                  </div>
                                </div>
                                <Button
                                  variant="ghost"
                                  size="icon"
                                  className="h-7 w-7 shrink-0 rounded-full hover:bg-primary/10 text-muted-foreground hover:text-primary"
                                  title="View PDF"
                                  disabled={viewingPdfKey === rowKey}
                                  onClick={(e) => handleViewPdf(doc, e)}
                                >
                                  {viewingPdfKey === rowKey ? (
                                    <Loader2 className="h-4 w-4 animate-spin" />
                                  ) : (
                                    <Eye className="h-4 w-4" />
                                  )}
                                </Button>
                                <Button
                                  variant="ghost"
                                  size="icon"
                                  className="h-7 w-7 shrink-0 rounded-full hover:bg-primary/10 text-primary"
                                  onClick={() => toggleRow(rowKey)}
                                >
                                  {isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                                </Button>
                              </div>
                              {isExpanded && (
                                <div className="border-t border-border px-3 pb-3 pt-0">
                                  <DetailPanel doc={doc} />
                                </div>
                              )}
                            </div>
                          )
                        })
                      )}
                    </div>

                    {/* Desktop table view */}
                    <div className="hidden md:block rounded-lg border border-border overflow-x-auto relative w-full">
                      <Table className="w-full table-fixed">
                        <TableHeader className="sticky top-0 z-10 bg-muted/80 backdrop-blur-sm">
                          <TableRow className="border-b-2 border-border hover:bg-transparent">
                            <TableHead className="w-10">
                              <Checkbox
                                checked={allFilteredChecked}
                                onCheckedChange={toggleAllChecked}
                                aria-label="Select all"
                              />
                            </TableHead>
                            <TableHead className="w-10"></TableHead>

                            {/* Explicit width (not just min-width) — table-fixed won't reliably
                                grant a floor to an unspecified-width column once sibling columns'
                                explicit widths already consume the container's full width. */}
                            <TableHead className="w-36">
                              <div className="flex items-center gap-1.5 font-semibold text-muted-foreground">
                                <Info className="h-3.5 w-3.5 text-primary" />
                                Description
                              </div>
                            </TableHead>
                            <TableHead className="w-32">
                              <div className="flex items-center gap-1.5 font-semibold text-muted-foreground">
                                <Info className="h-3.5 w-3.5 text-primary" />
                                Status
                              </div>
                            </TableHead>
                            <TableHead className="w-32">
                              <div className="flex items-center gap-1.5 font-semibold text-muted-foreground">
                                <User className="h-3.5 w-3.5 text-primary" />
                                Created By
                              </div>
                            </TableHead>
                            {!minimalView && (
                              <>
                                <TableHead className="w-28">
                                  <div className="flex items-center gap-1.5 font-semibold text-muted-foreground">
                                    <Building2 className="h-3.5 w-3.5 text-primary" />
                                    Office
                                  </div>
                                </TableHead>
                                <TableHead className="w-28">
                                  <div className="flex items-center gap-1.5 font-semibold text-muted-foreground">
                                    <Clock className="h-3.5 w-3.5 text-primary" />
                                    Last Action
                                  </div>
                                </TableHead>
                              </>
                            )}
                            <TableHead className="w-10 text-center">
                              <span className="font-semibold text-muted-foreground">View</span>
                            </TableHead>
                          </TableRow>
                        </TableHeader>
                        <TableBody>
                          {loadingDocs || loadingTypes ? (
                            Array.from({ length: 5 }).map((_, i) => (
                              <SkeletonRow key={i} minimal={minimalView} hasExpand />
                            ))
                          ) : filtered.length === 0 ? (
                            <TableRow className="hover:bg-transparent">
                              <TableCell colSpan={getColumnCount()} className="text-center py-16">
                                <div className="flex flex-col items-center gap-3">
                                  <div className="p-4 rounded-full bg-muted">
                                    <Inbox className="h-8 w-8 text-muted-foreground" />
                                  </div>
                                  <div>
                                    <p className="font-medium text-muted-foreground">No documents found</p>
                                    <p className="text-sm text-muted-foreground/70 mt-1">
                                      {searchQuery ? 'Try adjusting your search terms' : 'There are no pending documents for this category'}
                                    </p>
                                  </div>
                                </div>
                              </TableCell>
                            </TableRow>
                          ) : (
                            filtered.map((doc) => {
                              const rowKey = getRowKey(doc)
                              const isExpanded = expandedRows.has(rowKey)

                              return (
                                <Fragment key={rowKey}>
                                  <TableRow className="cursor-pointer hover:bg-accent/50 even:bg-muted/30 transition-colors">
                                    <TableCell className="w-10">
                                      <Checkbox
                                        checked={checkedDocs.has(rowKey)}
                                        onCheckedChange={() => toggleDocCheck(rowKey)}
                                        onClick={(e: React.MouseEvent) => e.stopPropagation()}
                                        aria-label={`Select ${doc.DocName}`}
                                      />
                                    </TableCell>
                                    <TableCell className="w-10">
                                      <Button
                                        variant="ghost"
                                        size="icon"
                                        className="h-7 w-7 rounded-full hover:bg-primary/10 text-primary"
                                        onClick={(e) => {
                                          e.stopPropagation()
                                          toggleRow(rowKey)
                                        }}
                                      >
                                        {isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                                      </Button>
                                    </TableCell>

                                    <TableCell className="max-w-0 w-full">
                                      {doc.DocDescription ? (
                                        <Tooltip>
                                          <TooltipTrigger className="block w-full min-w-0 text-left">
                                            <div className="font-medium text-sm truncate">{doc.DocDescription}</div>
                                          </TooltipTrigger>
                                          <TooltipContent><p>{doc.DocDescription}</p></TooltipContent>
                                        </Tooltip>
                                      ) : (
                                        <span className="text-sm text-muted-foreground">—</span>
                                      )}
                                    </TableCell>
                                    <TableCell className="max-w-0 w-full">
                                      <Tooltip>
                                        <TooltipTrigger className="block w-full min-w-0 text-left">
                                          <Badge variant="outline" className={`max-w-full truncate ${statusBadgeClass(doc.DocStatusName)}`}>
                                            {doc.DocStatusName ?? '—'}
                                          </Badge>
                                        </TooltipTrigger>
                                        <TooltipContent><p>{doc.DocStatusName ?? '—'}</p></TooltipContent>
                                      </Tooltip>
                                    </TableCell>
                                    <TableCell className="text-sm max-w-0 w-full">
                                      <Tooltip>
                                        <TooltipTrigger className="block w-full min-w-0 text-left">
                                          <div className="truncate">{doc.DocCreatedByName ?? '—'}</div>
                                        </TooltipTrigger>
                                        <TooltipContent><p>{doc.DocCreatedByName}</p></TooltipContent>
                                      </Tooltip>
                                      <div className="text-xs text-muted-foreground truncate">{doc.DocCreatedByPosition ?? ''}</div>
                                    </TableCell>
                                    {!minimalView && (
                                      <>
                                        <TableCell className="text-xs max-w-0 w-full">
                                          <Tooltip>
                                            <TooltipTrigger className="block w-full min-w-0 text-left">
                                              <div className="truncate">{doc.DocCreatedByOffice ?? '—'}</div>
                                            </TooltipTrigger>
                                            <TooltipContent><p>{doc.DocCreatedByOffice}</p></TooltipContent>
                                          </Tooltip>
                                        </TableCell>
                                        <TableCell className="text-xs max-w-0 w-full">
                                          <div>{formatDate(doc.LastActionDatetime)}</div>
                                          {doc.LastActionType && (
                                            <div className="text-muted-foreground truncate">
                                              {doc.LastActionType}{doc.LastActionByName && ` by ${doc.LastActionByName}`}
                                            </div>
                                          )}
                                        </TableCell>
                                      </>
                                    )}
                                    <TableCell className="text-center">
                                      <Button
                                        variant="ghost"
                                        size="icon"
                                        className="h-7 w-7 rounded-full hover:bg-primary/10 text-muted-foreground hover:text-primary"
                                        title="View PDF"
                                        disabled={viewingPdfKey === rowKey}
                                        onClick={(e) => handleViewPdf(doc, e)}
                                      >
                                        {viewingPdfKey === rowKey ? (
                                          <Loader2 className="h-4 w-4 animate-spin" />
                                        ) : (
                                          <Eye className="h-4 w-4" />
                                        )}
                                      </Button>
                                    </TableCell>
                                  </TableRow>
                                  {isExpanded && (
                                    <TableRow key={`${rowKey}-expanded`} className="bg-muted/30">
                                      <TableCell colSpan={getColumnCount()} className="p-0 border-t-0">
                                        <div className="py-4 px-3">
                                          <DetailPanel doc={doc} />
                                        </div>
                                      </TableCell>
                                    </TableRow>
                                  )}
                                </Fragment>
                              )
                            })
                          )}
                        </TableBody>
                      </Table>
                    </div>
                  </>
                )}
              </TabsContent>
            </Tabs>
          </CardContent>
        </Card>
      </div>

      {/* Batch Sign Dialog */}
      <Dialog open={batchSignOpen} onOpenChange={() => { /* backdrop/escape blocked — use close button */ }}>
        <DialogContent
          showCloseButton={false}
          className="
                    inset-0!
                    left-0! top-0!
                    translate-x-0! translate-y-0!
                    w-screen! h-dvh!
                    max-w-none!
                    p-0!
                    rounded-none!
                    flex flex-col
                " >
          <DialogHeader className="px-6 py-3 border-b flex flex-row items-center gap-3">
            <DialogTitle className="flex items-center gap-3 flex-1 min-w-0">
              {bulkSignMode
                ? 'Bulk Sign — Place signature on first document'
                : bulkProcessing
                  ? <span className="flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin text-primary" />Bulk Signing…</span>
                  : Object.keys(bulkDocStatuses).length > 0
                    ? (() => {
                      const signed = Object.values(bulkDocStatuses).filter(s => s.status === 'signed').length
                      const failed = Object.values(bulkDocStatuses).filter(s => s.status === 'failed').length
                      const total = checkedDocsList.length
                      return failed > 0
                        ? <span className="flex items-center gap-2">{signed}/{total} signed · <span className="text-destructive">{failed} failed</span></span>
                        : <span className="flex items-center gap-2 text-green-600 dark:text-green-400"><CheckCircle2 className="h-5 w-5" />All {total} documents signed</span>
                    })()
                    : batchSignedDocs.size === checkedDocsList.length && checkedDocsList.length > 0
                      ? <span className="flex items-center gap-2 text-green-600 dark:text-green-400"><CheckCircle2 className="h-5 w-5" />All documents signed</span>
                      : `Batch Sign — ${batchSignedDocs.size} / ${checkedDocsList.length} signed`}
            </DialogTitle>
            <Button
              variant="ghost"
              size="icon"
              className="shrink-0 ml-auto"
              disabled={bulkProcessing || bulkSignMode}
              title="Close"
              onClick={() => {
                if (bulkProcessingRef.current) return
                setBatchSignOpen(false)
                setBatchSidebarOpen(false)
                setPreVerified(null)
                preVerifiedRef.current = null
                setBulkSignMode(false)
                setBatchSignedDocs(new Set())
                setBulkDocStatuses({})
                fetchDocuments(activeTab)
              }}
            >
              <X className="h-4 w-4" />
            </Button>
          </DialogHeader>

          <div className="flex flex-1 overflow-hidden border-t">
            {/* Document Sidebar - collapsible on mobile */}
            <div className={`
                            fixed inset-y-0 left-0 z-20 w-70 transform transition-transform duration-300 ease-in-out
                            md:relative md:translate-x-0 md:w-[320px] md:shrink-0 md:border-r md:flex md:flex-col
                            bg-card shadow-lg md:shadow-none
                            ${batchSidebarOpen ? 'translate-x-0' : '-translate-x-full'}
                        `}>
              <div className="px-4 py-3 border-b bg-muted/50 flex items-center justify-between">
                <h3 className="font-semibold text-sm">
                  Documents ({batchSignedDocs.size}/{checkedDocsList.length})
                </h3>
                <Button
                  variant="ghost"
                  size="icon"
                  className="md:hidden h-8 w-8"
                  onClick={() => setBatchSidebarOpen(false)}
                >
                  <X className="h-4 w-4" />
                </Button>
              </div>
              {bulkSignMode && (
                <div className="px-4 py-2 bg-amber-50 dark:bg-amber-900/20 border-b border-amber-200 dark:border-amber-800 text-xs text-amber-800 dark:text-amber-300 flex items-start gap-1.5">
                  <AlertTriangle className="h-3.5 w-3.5 shrink-0 mt-0.5" />
                  <span>Sign the first document. The same location will be applied to all others.</span>
                </div>
              )}
              <ScrollArea className="flex-1">
                <div className="p-2 space-y-1">
                  {checkedDocsList.map((doc, idx) => {
                    const key = getRowKey(doc)
                    const isActive = key === batchActiveDocKey
                    const isSigned = batchSignedDocs.has(key)
                    const isFirstInBulk = bulkSignMode && idx === 0
                    const isDisabledInBulk = bulkSignMode && idx !== 0
                    const bulkStatus = bulkDocStatuses[key]
                    const isProcessing = bulkStatus?.status === 'signing'
                    const isBulkFailed = bulkStatus?.status === 'failed'
                    const isBulkPending = bulkStatus?.status === 'pending'
                    return (
                      <button
                        key={key}
                        type="button"
                        disabled={isDisabledInBulk || bulkProcessing}
                        onClick={() => {
                          if (isDisabledInBulk || bulkProcessing) return
                          setBatchActiveDocKey(key)
                          setBatchSidebarOpen(false)
                        }}
                        className={`w-full text-left rounded-lg p-3 transition-colors ${isDisabledInBulk || (bulkProcessing && !isActive)
                          ? 'opacity-60 cursor-not-allowed border border-transparent'
                          : isActive
                            ? 'bg-primary/10 border border-primary/30'
                            : 'hover:bg-muted border border-transparent'
                          }`}
                      >
                        <div className="flex items-start gap-2">
                          {/* Status icon */}
                          {isProcessing
                            ? <Loader2 className="h-4 w-4 mt-0.5 shrink-0 text-primary animate-spin" />
                            : isSigned
                              ? <CheckCircle2 className="h-4 w-4 mt-0.5 shrink-0 text-green-600 dark:text-green-400" />
                              : isBulkFailed
                                ? <XCircle className="h-4 w-4 mt-0.5 shrink-0 text-destructive" />
                                : isBulkPending
                                  ? <Circle className="h-4 w-4 mt-0.5 shrink-0 text-muted-foreground/50" />
                                  : <FileText className={`h-4 w-4 mt-0.5 shrink-0 ${isActive && !isDisabledInBulk ? 'text-primary' : 'text-muted-foreground'}`} />
                          }
                          <div className="min-w-0 flex-1">
                            <div className={`text-sm font-medium truncate ${isActive && !isDisabledInBulk && !isSigned ? 'text-primary' : ''}`}>
                              {doc.DocName ?? 'Untitled'}
                            </div>
                            <div className="text-xs text-muted-foreground truncate">
                              {doc.DocCode ?? '—'} · {doc.DocTypeAbbr ?? doc.DocTypeName ?? '—'}
                            </div>
                            <div className="text-xs text-muted-foreground truncate mt-0.5">
                              {doc.DocCreatedByName ?? '—'}
                            </div>
                            {/* Per-doc status label */}
                            {isProcessing && (
                              <div className="text-[10px] text-primary font-medium mt-0.5 animate-pulse">
                                Signing…
                              </div>
                            )}
                            {isBulkPending && (
                              <div className="text-[10px] text-muted-foreground mt-0.5">
                                Waiting…
                              </div>
                            )}
                            {isBulkFailed && (
                              <div className="text-[10px] text-destructive mt-0.5 leading-tight">
                                {bulkStatus.reason ?? 'Failed to sign'}
                              </div>
                            )}
                            {isSigned && !isProcessing && (
                              <div className="text-[10px] text-green-600 dark:text-green-400 font-medium mt-0.5">
                                {Object.keys(bulkDocStatuses).length > 0 ? 'Signed' : 'Signed — click to view'}
                              </div>
                            )}
                            {isFirstInBulk && (
                              <div className="text-[10px] text-amber-600 dark:text-amber-400 font-medium mt-0.5">
                                Sign this first
                              </div>
                            )}
                            {isDisabledInBulk && (
                              <div className="text-[10px] text-muted-foreground mt-0.5 italic">
                                Will use same location
                              </div>
                            )}
                          </div>
                        </div>
                      </button>
                    )
                  })}
                </div>
              </ScrollArea>
            </div>

            {/* Overlay for mobile sidebar */}
            {batchSidebarOpen && (
              <div
                className="fixed inset-0 bg-black/50 z-10 md:hidden"
                onClick={() => setBatchSidebarOpen(false)}
              />
            )}

            {/* PDF Viewer */}
            <div className="flex-1 flex flex-col min-w-0 relative">
              {/* PDF Toolbar - mobile: button to open sidebar */}
              <div className="border-b p-2 flex items-center gap-2 md:hidden">
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => setBatchSidebarOpen(true)}
                  className="h-8 w-8"
                >
                  <Menu className="h-5 w-5" />
                </Button>
                <span className="text-sm font-medium truncate">
                  {batchActiveDoc?.DocName ?? 'Select a document'}
                </span>
              </div>

              {/* PDF Pages */}
              <ScrollArea
                ref={pdfContainerRef}
                className="flex-1 bg-muted/30 p-4"
              >
                {batchActiveDoc ? (
                  <PDFSigningView
                    key={batchActiveDocKey}
                    doc_id={batchActiveDoc.DocId}
                    docDescription={batchActiveDoc.DocDescription ?? ''}
                    docStatus={batchActiveDoc.DocStatusName ?? ''}
                    sig_id={batchActiveDoc.SigId ?? 0}
                    sigLevel={batchActiveDoc.SigLevel ?? 0}
                    sigSignCount={batchActiveDoc.SigSignCount ?? 0}
                    preVerified={preVerified}
                    onConsumePreVerified={() => { }}
                    viewOnly={batchSignedDocs.has(batchActiveDocKey)}
                    showInfoToggle={false}
                    reloadAfterSave={
                      // Bulk mode never advances `batchActiveDocKey` away from the first
                      // document — the rest are signed in the background via
                      // handleBulkFirstDocSigned — so the first doc must always reload to
                      // show the real embedded signature instead of sitting on the stale,
                      // unsigned PDF with a "Signed" banner.
                      signingModeRef.current === 'bulk'
                        ? true
                        : !checkedDocsList.some(
                            d => getRowKey(d) !== batchActiveDocKey && !batchSignedDocs.has(getRowKey(d)),
                          )
                    }
                    onSignedWithLocation={
                      bulkSignMode
                        ? handleBulkFirstDocSigned
                        : () => handleBatchDocSigned()
                    }
                    signAsAlternate={
                      selectedPrincipal
                        ? { vwEids: String(selectedPrincipal.principalEid), vwUserType: String(selectedPrincipal.principalUserType) }
                        : undefined
                    }
                  />
                ) : (
                  <div className="flex items-center justify-center h-full">
                    <p className="text-muted-foreground">
                      Select a document to preview
                    </p>
                  </div>
                )}
              </ScrollArea>

              {/* Bulk processing overlay — covers PDF area, sidebar stays interactive */}
              {bulkProcessing && (
                <div className="absolute inset-0 bg-background/75 backdrop-blur-sm flex flex-col items-center justify-center z-20 pointer-events-auto">
                  <Loader2 className="h-12 w-12 animate-spin text-primary mb-4" />
                  <p className="font-semibold text-lg">Signing documents…</p>
                  <p className="text-sm text-muted-foreground mt-1">
                    Please wait — check the list on the left for live status.
                  </p>
                </div>
              )}
            </div>
          </div>
        </DialogContent>
      </Dialog>
      <SignatureVerifyDialog
        open={verifyDialogOpen}
        onOpenChange={setVerifyDialogOpen}
        hasPfxPassword={verifyHasPfx}
        cpNumber={baseCP}
        email={baseEMAIL}
        onVerify={handleVerified}
        loading={verifyChecking}
      />

      {/* Bulk Sign Warning Dialog */}
      <Dialog open={bulkWarningOpen} onOpenChange={setBulkWarningOpen}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-amber-600 dark:text-amber-400">
              <AlertTriangle className="h-5 w-5 shrink-0" />
              Bulk Sign Warning
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4 py-1">
            <p className="text-sm text-muted-foreground">
              You are about to bulk sign <strong className="text-foreground">{checkedDocsList.length}</strong> documents.
            </p>
            <div className="rounded-lg border border-amber-200 bg-amber-50 dark:bg-amber-900/20 dark:border-amber-800 p-4 space-y-2">
              <p className="text-sm font-semibold text-amber-800 dark:text-amber-300">How bulk sign works:</p>
              <ul className="text-sm text-amber-700 dark:text-amber-400 space-y-1.5 list-disc list-inside">
                <li>You will sign the <strong>first document</strong> and choose a signature location.</li>
                <li>That <strong>exact same page and position</strong> will be applied to all other selected documents automatically.</li>
                <li>If a document does not have that page number, it will be <strong>skipped</strong> and marked as unsuccessful.</li>
              </ul>
            </div>
            <p className="text-sm text-muted-foreground">Do you want to proceed?</p>
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setBulkWarningOpen(false)}>
              Cancel
            </Button>
            <Button
              className="bg-amber-500 hover:bg-amber-600 text-white"
              onClick={handleBulkWarningConfirm}
            >
              <Layers className="h-4 w-4 mr-1.5" />
              Proceed with Bulk Sign
            </Button>
          </div>
        </DialogContent>
      </Dialog>


    </div>
  )
}