import * as React from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogTrigger,
  DialogContent,
} from "@/components/ui/dialog"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  Upload,
  FileSignature,
  Fingerprint,
  Settings,
  Key,
  Lock,
  Image,
  FilePlus,
  Hand,
  Phone,
  CheckCircle2,
  ShieldCheck,
  Pencil,
  Type,
  Sparkles,
  History,
  Undo2,
} from 'lucide-react'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { baseEID, baseFULLNAME, baseUser_Type, authFetch, apiUrl } from '@/lib/config'
import RequestCertificate from '@/View/dgsign/request_certificate'
import UploadCertificate from '@/View/dgsign/upload_certificate'
import ChangeSpecimenDialog from '@/View/dgsign/change_specimen_dialog'
import AlternateSignatoriesCard from '@/View/dgsign/alternate_signatories_card'

// status: 0 = pending, 1 = approved, 3 = cancelled/discarded, 4 = for edit
// (returned, editable). 2 is intentionally unused.
const REQUEST_STATUS_LABEL: Record<number, string> = {
  0: "Pending",
  1: "Approved",
  3: "Cancelled/Discarded",
  4: "For Edit",
}
// source: 0 = Requested (server-generated), 1 = Uploaded (already had a cert)
const REQUEST_SOURCE_LABEL: Record<number, string> = {
  0: "Requested",
  1: "Uploaded",
}

interface MyRequestEntry {
  regId: number
  requestDate: string
  decidedDate: string | null
  status: number
  source: number
  remarks: string | null
}

interface MySpecimenHistoryEntry {
  specimenType: string
  changedAt: string
}

export const Route = createFileRoute('/_authenticated/signature/MySignature')({
  component: RouteComponent,
})

// Classic CSS checkerboard trick — signals "this PNG has a transparent
// background" the same way image editors do, without needing an asset.
const CHECKERBOARD_STYLE: React.CSSProperties = {
  backgroundImage:
    'linear-gradient(45deg, rgba(120,120,120,0.12) 25%, transparent 25%), ' +
    'linear-gradient(-45deg, rgba(120,120,120,0.12) 25%, transparent 25%), ' +
    'linear-gradient(45deg, transparent 75%, rgba(120,120,120,0.12) 75%), ' +
    'linear-gradient(-45deg, transparent 75%, rgba(120,120,120,0.12) 75%)',
  backgroundSize: '16px 16px',
  backgroundPosition: '0 0, 0 8px, 8px -8px, -8px 0px',
}

const LETTER_LINE_WIDTHS = [92, 100, 88, 96, 60, 100, 84, 70]

function RouteComponent() {
  const [activeTab, setActiveTab] = React.useState('signature')
  const [signatureImageUrl, setSignatureImageUrl] = React.useState<string | null>(null)
  const [initialImageUrl, setInitialImageUrl] = React.useState<string | null>(null)
  const [loadingSignature, setLoadingSignature] = React.useState(true)
  const [loadingInitial, setLoadingInitial] = React.useState(true)
  const [errorSignature, setErrorSignature] = React.useState(false)
  const [errorInitial, setErrorInitial] = React.useState(false)
  const [currentDate] = React.useState(new Date().toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric'
  }))

  const [myRequests, setMyRequests] = React.useState<MyRequestEntry[]>([])
  const [mySpecimenHistory, setMySpecimenHistory] = React.useState<MySpecimenHistoryEntry[]>([])
  const [loadingMyRequests, setLoadingMyRequests] = React.useState(true)
  const [resubmitRegId, setResubmitRegId] = React.useState<number | null>(null)

  const loadMyRequests = React.useCallback(async () => {
    setLoadingMyRequests(true)
    try {
      const res = await authFetch(apiUrl('DGSign/my_certificate_requests'))
      if (!res.ok) throw new Error('Failed to load request history')
      const data = await res.json()
      setMyRequests(data.requests ?? [])
      setMySpecimenHistory(data.specimenChanges ?? [])
    } catch (err) {
      console.error('Error fetching my certificate requests:', err)
    } finally {
      setLoadingMyRequests(false)
    }
  }, [])

  React.useEffect(() => {
    loadMyRequests()
  }, [loadMyRequests])

  // Fetch signature image
  React.useEffect(() => {
    const fetchSignature = async () => {
      try {
        setLoadingSignature(true)
        const url = apiUrl(`DGSign/get_signature_image_merged?eids=${baseEID}&usertypes=${baseUser_Type}&type=signature`)
        const response = await authFetch(url)
        if (response.status === 404) return // no specimen uploaded yet — not an error
        if (!response.ok) throw new Error('Failed to fetch signature')
        const blob = await response.blob()
        const objectUrl = URL.createObjectURL(blob)
        setSignatureImageUrl(objectUrl)
      } catch (err) {
        console.error('Error fetching signature:', err)
        setErrorSignature(true)
      } finally {
        setLoadingSignature(false)
      }
    }
    fetchSignature()
  }, [])

  // Fetch initial signature image
  React.useEffect(() => {
    const fetchInitial = async () => {
      try {
        setLoadingInitial(true)
        const url = apiUrl(`DGSign/get_signature_image_merged?eids=${baseEID}&usertypes=${baseUser_Type}&type=initial`)
        const response = await authFetch(url)
        if (response.status === 404) return // no specimen uploaded yet — not an error
        if (!response.ok) throw new Error('Failed to fetch initial signature')
        const blob = await response.blob()
        const objectUrl = URL.createObjectURL(blob)
        setInitialImageUrl(objectUrl)
      } catch (err) {
        console.error('Error fetching initial signature:', err)
        setErrorInitial(true)
      } finally {
        setLoadingInitial(false)
      }
    }
    fetchInitial()
  }, [])

  // Dialog state
  const [openRequestDialog, setOpenRequestDialog] = React.useState(false)
  const [openUploadDialog, setOpenUploadDialog] = React.useState(false)
  const [openChangeSignatureDialog, setOpenChangeSignatureDialog] = React.useState(false)
  const [openChangeInitialDialog, setOpenChangeInitialDialog] = React.useState(false)

  const handleRegisterBiometric = () => console.log('Register Biometric')
  const handleDevices = () => console.log('Devices')
  const handleForgotPassword = () => console.log('Forgot Password')
  const handleRemoveSavedPassword = () => console.log('Remove Saved Password')
  const handleChangePin = () => console.log('Change PIN')
  const handleSignatureSpecimenSaved = (previewUrl: string) => {
    setSignatureImageUrl(previewUrl)
    setErrorSignature(false)
    loadMyRequests()
  }
  const handleInitialSpecimenSaved = (previewUrl: string) => {
    setInitialImageUrl(previewUrl)
    setErrorInitial(false)
    loadMyRequests()
  }
  const handleEditRequest = (entry: MyRequestEntry) => {
    setResubmitRegId(entry.regId)
    if (entry.source === 1) setOpenUploadDialog(true)
    else setOpenRequestDialog(true)
  }
  const handleChangeSignatureSpecimen = () => setOpenChangeSignatureDialog(true)
  const handleAddInitialSpecimen = () => setOpenChangeInitialDialog(true)

  return (
    <div className="relative min-h-screen overflow-hidden bg-linear-to-br from-slate-50 to-slate-100 dark:from-slate-950 dark:to-slate-900 py-8 px-4">
      {/* Futuristic ambient backdrop: soft glow blobs + faint dot grid */}
      <div className="pointer-events-none absolute inset-0 overflow-hidden">
        <div className="absolute -top-24 -left-24 h-80 w-80 rounded-full bg-primary/10 blur-3xl" />
        <div className="absolute top-1/3 -right-24 h-96 w-96 rounded-full bg-indigo-500/10 blur-3xl" />
        <div
          className="absolute inset-0 opacity-40 dark:opacity-20"
          style={{
            backgroundImage: 'radial-gradient(currentColor 1px, transparent 1px)',
            backgroundSize: '28px 28px',
            color: 'rgb(148 163 184 / 0.3)',
          }}
        />
      </div>

      <div className="relative container max-w-7xl mx-auto space-y-6">
        {/* Breadcrumb */}
        <nav className="text-sm text-muted-foreground flex items-center gap-1">
          <span className="hover:text-foreground cursor-pointer transition-colors">Home Page</span>
          <span>/</span>
          <span className="text-foreground font-medium">Digital Signature</span>
        </nav>

        {/* Main Panel */}
        <Card className="border-0 shadow-xl bg-white/80 backdrop-blur-sm dark:bg-slate-900/80 rounded-2xl overflow-hidden">
          <CardContent className="p-6 space-y-6">
            {/* Header with icon and title */}
            <div className="flex items-center gap-3 pb-2 border-b border-border/40">
              <div className="p-2 bg-linear-to-br from-primary/20 to-primary/5 rounded-xl">
                <FileSignature className="h-6 w-6 text-primary" />
              </div>
              <div>
                <h2 className="text-2xl font-bold tracking-tight">Digital Signature</h2>
                <p className="text-sm text-muted-foreground flex items-center gap-1">
                  <Sparkles className="h-3 w-3" />
                  Manage your certificate and signature specimens
                </p>
              </div>
            </div>

            {/* Action Buttons - Enhanced with gradients and hover effects */}
            <div className="flex flex-wrap gap-3">
              <Dialog
                open={openUploadDialog}
                onOpenChange={(open) => { setOpenUploadDialog(open); if (!open) setResubmitRegId(null) }}
              >
                <DialogTrigger
                  render={
                    <Button className="bg-linear-to-r from-amber-500 to-orange-500 hover:from-amber-600 hover:to-orange-600 text-white shadow-md hover:shadow-lg transition-all" />
                  }
                >
                  <Upload className="mr-2 h-4 w-4" />
                  Upload Certificate
                </DialogTrigger>
                <DialogContent className="max-w-4xl w-full sm:max-w-4xl max-h-[90vh] p-0 gap-0 overflow-hidden">
                  <UploadCertificate
                    resubmitRegId={resubmitRegId ?? undefined}
                    onSuccess={() => { setOpenUploadDialog(false); setResubmitRegId(null); loadMyRequests() }}
                    onCancel={() => { setOpenUploadDialog(false); setResubmitRegId(null) }}
                  />
                </DialogContent>
              </Dialog>

              <Dialog
                open={openRequestDialog}
                onOpenChange={(open) => { setOpenRequestDialog(open); if (!open) setResubmitRegId(null) }}
              >
                <DialogTrigger
                  render={
                    <Button className="bg-linear-to-r from-indigo-500 to-indigo-600 hover:from-indigo-600 hover:to-indigo-700 shadow-md hover:shadow-lg" />
                  }
                >
                  <FilePlus className="mr-2 h-4 w-4" />
                  Request Certificate
                </DialogTrigger>
                <DialogContent className="max-w-4xl w-full sm:max-w-4xl max-h-[90vh] p-0 gap-0 overflow-hidden">
                  <RequestCertificate
                    resubmitRegId={resubmitRegId ?? undefined}
                    onSuccess={() => { setOpenRequestDialog(false); setResubmitRegId(null); loadMyRequests() }}
                    onCancel={() => { setOpenRequestDialog(false); setResubmitRegId(null) }}
                  />
                </DialogContent>
              </Dialog>
              {/* Bio Options Dropdown */}
              <DropdownMenu>
                <DropdownMenuTrigger>
                  <span
                    role="button"
                    tabIndex={0}
                    className="inline-flex items-center rounded-md bg-linear-to-r from-emerald-600 to-teal-600 px-3 py-1 shadow-md hover:opacity-95 cursor-pointer"
                  >
                    <Fingerprint className="mr-2 h-4 w-4" />
                    Bio Options
                  </span>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" className="w-48">
                  <DropdownMenuItem onClick={handleRegisterBiometric} className="cursor-pointer">
                    <Hand className="mr-2 h-4 w-4" />
                    <span>Register</span>
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleDevices} className="cursor-pointer">
                    <Phone className="mr-2 h-4 w-4" />
                    <span>Devices</span>
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>

              {/* Other Options Dropdown */}
              <DropdownMenu>
                <DropdownMenuTrigger>
                  <span
                    role="button"
                    tabIndex={0}
                    className="inline-flex items-center rounded-md border border-slate-300 dark:border-slate-700 px-3 py-1 shadow-sm hover:shadow-md cursor-pointer"
                  >
                    <Settings className="mr-2 h-4 w-4" />
                    Other Options
                  </span>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" className="w-56">
                  <DropdownMenuItem onClick={handleForgotPassword} className="cursor-pointer">
                    <Key className="mr-2 h-4 w-4" />
                    Forgot Signature Password
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleRemoveSavedPassword} className="cursor-pointer">
                    <Lock className="mr-2 h-4 w-4" />
                    Remove Saved Password
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleChangePin} className="cursor-pointer">
                    <Key className="mr-2 h-4 w-4" />
                    Change Signature PIN Code
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleChangeSignatureSpecimen} className="cursor-pointer">
                    <FileSignature className="mr-2 h-4 w-4" />
                    Change Signature Specimen
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={handleAddInitialSpecimen} className="cursor-pointer">
                    <Image className="mr-2 h-4 w-4" />
                    Add Initial Signature Specimen
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </div>

            {/* Tabs */}
            <Tabs value={activeTab} onValueChange={setActiveTab} className="w-full">
              <TabsList className="grid w-full max-w-md grid-cols-2 mb-4 bg-muted/50 p-1 rounded-full">
                <TabsTrigger value="signature" className="rounded-full gap-1.5 data-[state=active]:bg-background data-[state=active]:shadow-sm">
                  <FileSignature className="h-3.5 w-3.5" />
                  My Signature
                </TabsTrigger>
                <TabsTrigger value="initial" className="rounded-full gap-1.5 data-[state=active]:bg-background data-[state=active]:shadow-sm">
                  <Type className="h-3.5 w-3.5" />
                  Initial Signature
                </TabsTrigger>
              </TabsList>

              <TabsContent value="signature">
                <SignatureShowcase
                  loading={loadingSignature}
                  error={errorSignature}
                  imageUrl={signatureImageUrl}
                  fullName={baseFULLNAME}
                  date={currentDate}
                  emptyLabel="No signature uploaded yet."
                  emptyActionLabel="Add Signature Specimen"
                  onChangeSpecimen={handleChangeSignatureSpecimen}
                />
              </TabsContent>

              <TabsContent value="initial">
                <SignatureShowcase
                  loading={loadingInitial}
                  error={errorInitial}
                  imageUrl={initialImageUrl}
                  fullName={baseFULLNAME}
                  date={currentDate}
                  emptyLabel="No initial signature uploaded yet."
                  emptyActionLabel="Add Initial Specimen"
                  onChangeSpecimen={handleAddInitialSpecimen}
                />
              </TabsContent>
            </Tabs>
          </CardContent>
        </Card>

        {/* My Requests — certificate request/upload history, plus specimen
            change history. Returned requests can be edited and resubmitted
            without starting a brand-new request. */}
        <Card className="border-0 shadow-xl bg-white/80 backdrop-blur-sm dark:bg-slate-900/80 rounded-2xl overflow-hidden">
          <CardHeader>
            <CardTitle className="text-base flex items-center gap-2">
              <History className="h-4 w-4 text-primary" />
              My Requests
            </CardTitle>
            <CardDescription>
              Your certificate requests and uploads, and when you changed your signature specimens.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {loadingMyRequests ? (
              <div className="space-y-2">
                <Skeleton className="h-10 w-full" />
                <Skeleton className="h-10 w-full" />
              </div>
            ) : myRequests.length === 0 && mySpecimenHistory.length === 0 ? (
              <p className="py-6 text-center text-sm text-muted-foreground">
                No certificate requests or specimen changes yet.
              </p>
            ) : (
              <div className="space-y-6">
                {myRequests.length > 0 && (
                  <div className="rounded-lg border overflow-hidden">
                    <div className="overflow-x-auto">
                      <Table>
                        <TableHeader>
                          <TableRow className="bg-muted/40 hover:bg-muted/40">
                            <TableHead>Source</TableHead>
                            <TableHead>Requested</TableHead>
                            <TableHead>Decided</TableHead>
                            <TableHead>Status</TableHead>
                            <TableHead>Remarks</TableHead>
                            <TableHead className="text-center">Action</TableHead>
                          </TableRow>
                        </TableHeader>
                        <TableBody>
                          {myRequests.map((entry) => (
                            <TableRow key={entry.regId} className="hover:bg-muted/30 transition-colors">
                              <TableCell className="text-sm">{REQUEST_SOURCE_LABEL[entry.source] ?? "—"}</TableCell>
                              <TableCell className="text-sm whitespace-nowrap">
                                {new Date(entry.requestDate).toLocaleDateString("en-US", { year: "numeric", month: "short", day: "numeric" })}
                              </TableCell>
                              <TableCell className="text-sm whitespace-nowrap">
                                {entry.decidedDate ? new Date(entry.decidedDate).toLocaleDateString("en-US", { year: "numeric", month: "short", day: "numeric" }) : "—"}
                              </TableCell>
                              <TableCell>
                                <Badge
                                  variant={entry.status === 1 ? "default" : entry.status === 0 ? "secondary" : "outline"}
                                  className="text-[10px]"
                                >
                                  {REQUEST_STATUS_LABEL[entry.status] ?? entry.status}
                                </Badge>
                              </TableCell>
                              <TableCell className="text-sm max-w-60 truncate" title={entry.remarks ?? undefined}>
                                {entry.remarks || "—"}
                              </TableCell>
                              <TableCell className="text-center">
                                {entry.status === 4 ? (
                                  <Button size="sm" variant="outline" className="gap-1.5" onClick={() => handleEditRequest(entry)}>
                                    <Undo2 className="h-3.5 w-3.5" />
                                    Edit & Resubmit
                                  </Button>
                                ) : (
                                  <span className="text-xs text-muted-foreground">—</span>
                                )}
                              </TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    </div>
                  </div>
                )}

                {mySpecimenHistory.length > 0 && (
                  <div>
                    <p className="text-sm font-semibold flex items-center gap-1.5 mb-2">
                      <FileSignature className="h-3.5 w-3.5 text-primary" />
                      Specimen Changes
                    </p>
                    <div className="rounded-lg border divide-y">
                      {mySpecimenHistory.map((s, i) => (
                        <div key={i} className="flex items-center justify-between px-3 py-2 text-sm">
                          <span className="text-muted-foreground capitalize">{s.specimenType} specimen changed</span>
                          <span className="text-xs text-muted-foreground">{new Date(s.changedAt).toLocaleString()}</span>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            )}
          </CardContent>
        </Card>

        <AlternateSignatoriesCard />
      </div>

      <ChangeSpecimenDialog
        open={openChangeSignatureDialog}
        onOpenChange={setOpenChangeSignatureDialog}
        type="signature"
        currentImageUrl={signatureImageUrl}
        onSaved={handleSignatureSpecimenSaved}
      />
      <ChangeSpecimenDialog
        open={openChangeInitialDialog}
        onOpenChange={setOpenChangeInitialDialog}
        type="initial"
        currentImageUrl={initialImageUrl}
        onSaved={handleInitialSpecimenSaved}
      />
    </div>
  )
}

function SignatureShowcase({
  loading,
  error,
  imageUrl,
  fullName,
  date,
  emptyLabel,
  emptyActionLabel,
  onChangeSpecimen,
}: {
  loading: boolean
  error: boolean
  imageUrl: string | null
  fullName: string
  date: string
  emptyLabel: string
  emptyActionLabel: string
  onChangeSpecimen: () => void
}) {
  const hasImage = !loading && !error && !!imageUrl

  return (
    <div className="grid gap-6 lg:grid-cols-5 items-stretch">
      {/* Document preview — shows how this specimen is stamped onto a signed paper/PDF */}
      <div className="lg:col-span-3 flex flex-col items-center">
        <div className="relative w-full max-w-sm aspect-8/11 rounded-2xl bg-white shadow-2xl ring-1 ring-black/5 overflow-hidden">
          {/* Simulated letterhead / body text */}
          <div className="absolute inset-0 p-7 flex flex-col">
            <div className="h-2.5 w-10 rounded-sm bg-slate-300" />
            <div className="h-1.5 w-24 rounded-full bg-slate-200 mt-1.5" />
            <div className="mt-6 space-y-2">
              {LETTER_LINE_WIDTHS.map((w, i) => (
                <div
                  key={i}
                  className="h-1.5 rounded-full bg-slate-100"
                  style={{ width: `${w}%` }}
                />
              ))}
            </div>
          </div>

          {/* Signature stamp block — mirrors pdf_viewer's actual draggable
              sign-off layout: the signature image spans the full stamp
              width as a backdrop, with the "Digitally signed by / Name /
              Date" text overlaid starting at the horizontal midpoint
              (left:95 of 190 in pdf_viewer.tsx — mirrors Spire's
              SignImageAndSignDetail layout used on the real PDF). */}
          <div className="absolute bottom-6 right-6 w-52 sm:w-60 rounded-lg border border-slate-200 bg-slate-50/90 shadow-sm overflow-hidden">
            <div className="relative w-full aspect-[19/5]">
              <div className="absolute inset-0 flex items-center justify-center">
                {hasImage ? (
                  <img src={imageUrl!} alt="Signature preview" className="w-full h-full object-contain" />
                ) : (
                  <span className="text-[9px] italic text-slate-300">signature</span>
                )}
              </div>
              <div
                className="absolute left-1/2 leading-tight whitespace-nowrap overflow-hidden text-ellipsis"
                style={{ top: '10%', width: 'calc(50% - 6px)' }}
              >
                <p className="text-[7px] sm:text-[8px] text-slate-600">Digitally signed by:</p>
                <p className="text-[7px] sm:text-[8px] font-semibold text-slate-800 truncate mt-0.5">{fullName || 'Your Name'}</p>
                <p className="text-[7px] sm:text-[8px] text-slate-500 mt-0">Date: {date}</p>
              </div>
            </div>
          </div>
        </div>
        <p className="mt-3 text-xs text-muted-foreground text-center">
          Preview — how this specimen appears on a signed document
        </p>
      </div>

      {/* Specimen viewer + status */}
      <div className="lg:col-span-2">
        <div className="h-full flex flex-col rounded-2xl border bg-linear-to-br from-white to-slate-50 dark:from-slate-950 dark:to-slate-900 shadow-lg p-5">
          <div className="flex items-center gap-2 flex-wrap mb-4">
            <Badge variant="outline" className="bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400 border-green-200 dark:border-green-800">
              <CheckCircle2 className="h-3 w-3 mr-1" />
              Active
            </Badge>
            <Badge variant="secondary" className="gap-1">
              <ShieldCheck className="h-3 w-3" />
              Verified
            </Badge>
          </div>

          <div
            className="flex-1 min-h-48 rounded-xl border border-dashed border-border/60 flex items-center justify-center p-4"
            style={hasImage ? CHECKERBOARD_STYLE : undefined}
          >
            {loading ? (
              <Skeleton className="h-full w-full rounded-lg" />
            ) : error ? (
              <Alert variant="destructive" className="border-0 bg-transparent p-0">
                <AlertDescription>Failed to load signature image. Please try again later.</AlertDescription>
              </Alert>
            ) : hasImage ? (
              <img
                src={imageUrl!}
                alt="Signature specimen"
                className="max-h-48 max-w-full object-contain"
              />
            ) : (
              <p className="text-sm text-muted-foreground text-center">{emptyLabel}</p>
            )}
          </div>

          <Button
            variant={hasImage ? "outline" : "default"}
            size="sm"
            className="mt-4 self-start"
            onClick={onChangeSpecimen}
          >
            {hasImage ? (
              <>
                <Pencil className="mr-2 h-3.5 w-3.5" />
                Change Specimen
              </>
            ) : (
              <>
                <Upload className="mr-2 h-3.5 w-3.5" />
                {emptyActionLabel}
              </>
            )}
          </Button>
        </div>
      </div>
    </div>
  )
}
