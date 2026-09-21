import { useCallback, useEffect, useState } from 'react'
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from '@/components/ui/card'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Users, Plus, FileText, Trash2, Loader2, Pencil } from 'lucide-react'
import { toast } from 'sonner'
import { apiUrl, authFetch } from '@/lib/config'
import DynamicMultiSelect, { DynamicMultiModel } from '@/View/dynamic/DynamicMultiSelect'
import AlternateDocumentTypesDialog from '@/View/dgsign/alternate_document_types_dialog'

interface Alternate {
  id: number
  alternateEid: number
  alternateUserType: number
  alternateName: string | null
  alternateOffice: string | null
  isActive: boolean
  isPermanent: boolean
  dateFrom: string | null
  dateTo: string | null
}

// Compares calendar dates only (YYYY-MM-DD substrings), never full Date
// instants. dateFrom/dateTo come from the server as date-only values; parsing
// them with `new Date(...)` treats them as UTC midnight, which makes
// `now <= new Date(dateTo)` false for nearly all of the actual "dateTo"
// calendar day in any timezone behind UTC. Taking the local YYYY-MM-DD for
// "now" and the raw YYYY-MM-DD substring for the bounds keeps this correct
// regardless of the viewer's timezone, without needing a date library.
function toLocalDateOnly(d: Date): string {
  const year = d.getFullYear()
  const month = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function isCurrentlyActive(a: Alternate): boolean {
  if (!a.isActive) return false
  if (a.isPermanent) return true
  if (!a.dateFrom || !a.dateTo) return false
  const today = toLocalDateOnly(new Date())
  const from = a.dateFrom.slice(0, 10)
  const to = a.dateTo.slice(0, 10)
  return today >= from && today <= to
}

export default function AlternateSignatoriesCard() {
  const [alternates, setAlternates] = useState<Alternate[]>([])
  const [loading, setLoading] = useState(true)
  const [addOpen, setAddOpen] = useState(false)
  const [editing, setEditing] = useState<Alternate | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<Alternate | null>(null)
  const [docTypesTarget, setDocTypesTarget] = useState<Alternate | null>(null)

  const loadAlternates = useCallback(async () => {
    setLoading(true)
    try {
      const res = await authFetch(apiUrl('AlternateSignatories/my_alternates'))
      if (!res.ok) throw new Error('Failed to load alternates')
      const data: Alternate[] = await res.json()
      setAlternates(data)
    } catch (err) {
      console.error(err)
      toast.error('Failed to load your alternate signatories.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadAlternates()
  }, [loadAlternates])

  return (
    <Card className="border-0 shadow-xl bg-white/80 backdrop-blur-sm dark:bg-slate-900/80 rounded-2xl overflow-hidden">
      <CardHeader className="flex flex-row items-center justify-between">
        <div>
          <CardTitle className="text-base flex items-center gap-2">
            <Users className="h-4 w-4 text-primary" />
            Alternate Signatories
          </CardTitle>
          <CardDescription>
            People you allow to sign specific document types on your behalf.
          </CardDescription>
        </div>
        <Dialog open={addOpen} onOpenChange={setAddOpen}>
          <DialogTrigger render={<Button size="sm" className="gap-1.5" />}>
            <Plus className="h-3.5 w-3.5" />
            Add Alternate
          </DialogTrigger>
          {addOpen && (
            <AddEditAlternateDialog
              editing={null}
              onClose={() => setAddOpen(false)}
              onSaved={() => { setAddOpen(false); loadAlternates() }}
            />
          )}
        </Dialog>
      </CardHeader>
      <CardContent>
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
          </div>
        ) : alternates.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">
            You haven't set up any alternate signatories yet.
          </p>
        ) : (
          <div className="rounded-lg border overflow-hidden">
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow className="bg-muted/40 hover:bg-muted/40">
                    <TableHead>Name</TableHead>
                    <TableHead>Office</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead>Validity</TableHead>
                    <TableHead className="text-center">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {alternates.map((a) => (
                    <TableRow key={a.id} className="hover:bg-muted/30 transition-colors">
                      <TableCell className="text-sm font-medium">{a.alternateName ?? '—'}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{a.alternateOffice ?? '—'}</TableCell>
                      <TableCell>
                        <Badge variant={isCurrentlyActive(a) ? 'default' : 'secondary'} className="text-[10px]">
                          {isCurrentlyActive(a) ? 'Active' : 'Inactive'}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-sm">
                        {a.isPermanent
                          ? 'Permanent'
                          : a.dateFrom && a.dateTo
                            ? `${new Date(a.dateFrom).toLocaleDateString()} – ${new Date(a.dateTo).toLocaleDateString()}`
                            : '—'}
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center justify-center gap-1">
                          <Button size="icon" variant="ghost" className="h-7 w-7" title="Edit" onClick={() => setEditing(a)}>
                            <Pencil className="h-3.5 w-3.5" />
                          </Button>
                          <Button size="icon" variant="ghost" className="h-7 w-7" title="Document Types" onClick={() => setDocTypesTarget(a)}>
                            <FileText className="h-3.5 w-3.5" />
                          </Button>
                          <Button
                            size="icon"
                            variant="ghost"
                            className="h-7 w-7 text-destructive hover:text-destructive"
                            title="Delete"
                            onClick={() => setDeleteTarget(a)}
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </div>
        )}
      </CardContent>

      <Dialog open={editing !== null} onOpenChange={(v) => { if (!v) setEditing(null) }}>
        <AddEditAlternateDialog
          editing={editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); loadAlternates() }}
        />
      </Dialog>

      <DeleteAlternateDialog
        target={deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onDeleted={() => { setDeleteTarget(null); loadAlternates() }}
      />

      <AlternateDocumentTypesDialog
        open={docTypesTarget !== null}
        onOpenChange={(v) => { if (!v) setDocTypesTarget(null) }}
        alternateId={docTypesTarget?.id ?? null}
        alternateName={docTypesTarget?.alternateName ?? ''}
      />
    </Card>
  )
}

function AddEditAlternateDialog({
  editing,
  onClose,
  onSaved,
}: {
  editing: Alternate | null
  onClose: () => void
  onSaved: () => void
}) {
  const [pickedId, setPickedId] = useState<string | number | null>(null)
  const [pickedEmployee, setPickedEmployee] = useState<{ eid: number; userType: number; name: string } | null>(null)
  const [isPermanent, setIsPermanent] = useState(true)
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (editing) {
      setIsPermanent(editing.isPermanent)
      setDateFrom(editing.dateFrom ? editing.dateFrom.substring(0, 10) : '')
      setDateTo(editing.dateTo ? editing.dateTo.substring(0, 10) : '')
    } else {
      setPickedId(null)
      setPickedEmployee(null)
      setIsPermanent(true)
      setDateFrom('')
      setDateTo('')
    }
  }, [editing])

  // DynamicMultiSelect is reused here as a single-select: whenever a new item
  // is toggled on, only the most-recently-toggled id is kept, which visually
  // replaces the previous selection instead of adding to it.
  const handleEmployeePick = (ids: (string | number)[], items: DynamicMultiModel[]) => {
    const newestId = ids.length > 0 ? ids[ids.length - 1] : null
    setPickedId(newestId)
    if (newestId === null) { setPickedEmployee(null); return }
    const item = items.find((i) => String(i.id) === String(newestId))
    if (!item) { setPickedEmployee(null); return }
    setPickedEmployee({
      eid: Number(item.additional_id ?? 0),
      userType: Number(item['additional_Id_two'] ?? 0),
      name: item.value,
    })
  }

  const handleSave = async () => {
    if (!editing && !pickedEmployee) {
      toast.error('Pick an employee to add as your alternate.')
      return
    }
    if (!isPermanent && (!dateFrom || !dateTo)) {
      toast.error('Set both a start and end date, or mark this alternate as permanent.')
      return
    }

    setSaving(true)
    try {
      const body = {
        isPermanent,
        dateFrom: isPermanent ? null : dateFrom,
        dateTo: isPermanent ? null : dateTo,
      }
      const res = editing
        ? await authFetch(apiUrl(`AlternateSignatories/update_alternate/${editing.id}`), {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
          })
        : await authFetch(apiUrl('AlternateSignatories/add_alternate'), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ...body, alternateEid: pickedEmployee!.eid, alternateUserType: pickedEmployee!.userType }),
          })

      const data = await res.json().catch(() => null)
      if (!res.ok || !data?.success) throw new Error(data?.message ?? 'Failed to save alternate')
      toast.success(editing ? 'Alternate updated.' : 'Alternate added.')
      onSaved()
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to save alternate.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <DialogContent className="max-w-md">
      <DialogHeader>
        <DialogTitle>{editing ? 'Edit Alternate' : 'Add Alternate'}</DialogTitle>
        <DialogDescription>
          {editing
            ? `Change the validity window for ${editing.alternateName ?? ''}.`
            : 'Pick an employee to authorize as your alternate signatory.'}
        </DialogDescription>
      </DialogHeader>

      <div className="space-y-4">
        {!editing && (
          <div className="space-y-1.5">
            <Label className="text-sm">Employee</Label>
            <DynamicMultiSelect
              api={apiUrl('References/get_listofSignatories')}
              placeholder="Search for an employee..."
              value={pickedId !== null ? [pickedId] : []}
              showSelectAll={false}
              maxBadges={1}
              onChangeCallback={handleEmployeePick}
            />
          </div>
        )}

        <div className="flex items-center justify-between">
          <Label className="text-sm">Permanent</Label>
          <Switch checked={isPermanent} onCheckedChange={setIsPermanent} />
        </div>

        {!isPermanent && (
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label className="text-sm">From</Label>
              <input
                type="date"
                value={dateFrom}
                onChange={(e) => setDateFrom(e.target.value)}
                className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
              />
            </div>
            <div className="space-y-1.5">
              <Label className="text-sm">To</Label>
              <input
                type="date"
                value={dateTo}
                onChange={(e) => setDateTo(e.target.value)}
                className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
              />
            </div>
          </div>
        )}
      </div>

      <DialogFooter>
        <Button variant="outline" onClick={onClose} disabled={saving}>Cancel</Button>
        <Button onClick={handleSave} disabled={saving} className="gap-1.5">
          {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
          {editing ? 'Save Changes' : 'Add Alternate'}
        </Button>
      </DialogFooter>
    </DialogContent>
  )
}

function DeleteAlternateDialog({
  target,
  onClose,
  onDeleted,
}: {
  target: Alternate | null
  onClose: () => void
  onDeleted: () => void
}) {
  const [deleting, setDeleting] = useState(false)

  const handleConfirm = async () => {
    if (!target) return
    setDeleting(true)
    try {
      const res = await authFetch(apiUrl(`AlternateSignatories/delete_alternate/${target.id}`), { method: 'DELETE' })
      const data = await res.json().catch(() => null)
      if (!res.ok || !data?.success) throw new Error(data?.message ?? 'Failed to delete alternate')
      toast.success('Alternate removed.')
      onDeleted()
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to delete alternate.')
    } finally {
      setDeleting(false)
    }
  }

  return (
    <Dialog open={!!target} onOpenChange={(v) => { if (!v) onClose() }}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Remove Alternate</DialogTitle>
          <DialogDescription>
            {target
              ? `This permanently removes ${target.alternateName ?? ''} as your alternate, along with the document types you granted them.`
              : ''}
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={deleting}>Cancel</Button>
          <Button variant="destructive" onClick={handleConfirm} disabled={deleting} className="gap-1.5">
            {deleting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
            Delete
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
