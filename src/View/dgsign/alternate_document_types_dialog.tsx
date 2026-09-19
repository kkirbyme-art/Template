import { useCallback, useEffect, useState } from 'react'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Loader2, Trash2, FileText } from 'lucide-react'
import { toast } from 'sonner'
import { apiUrl, authFetch } from '@/lib/config'
import DynamicMultiSelect from '@/View/dynamic/DynamicMultiSelect'

interface AssignedDocType {
  id: number
  docTypeId: number
  documentDescription: string | null
  documentAbbr: string | null
}

export default function AlternateDocumentTypesDialog({
  open,
  onOpenChange,
  alternateId,
  alternateName,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  alternateId: number | null
  alternateName: string
}) {
  const [assigned, setAssigned] = useState<AssignedDocType[]>([])
  const [loading, setLoading] = useState(false)
  const [removingId, setRemovingId] = useState<number | null>(null)

  const loadAssigned = useCallback(async () => {
    if (alternateId === null) return
    setLoading(true)
    try {
      const res = await authFetch(apiUrl(`AlternateSignatories/alternate_document_types/${alternateId}`))
      if (!res.ok) throw new Error('Failed to load document types')
      const data: AssignedDocType[] = await res.json()
      setAssigned(data)
    } catch (err) {
      console.error(err)
      toast.error('Failed to load document types for this alternate.')
    } finally {
      setLoading(false)
    }
  }, [alternateId])

  useEffect(() => {
    if (open) loadAssigned()
  }, [open, loadAssigned])

  const handleAdd = async (ids: (string | number)[]) => {
    if (alternateId === null || ids.length === 0) return
    const alreadyIds = new Set(assigned.map((a) => String(a.docTypeId)))
    const newIds = ids.filter((id) => !alreadyIds.has(String(id)))
    if (newIds.length === 0) return

    for (const docTypeId of newIds) {
      try {
        const res = await authFetch(apiUrl('AlternateSignatories/add_document_type'), {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ alterId: alternateId, docTypeId: Number(docTypeId) }),
        })
        const data = await res.json().catch(() => null)
        if (!res.ok || !data?.success) throw new Error(data?.message ?? 'Failed to add document type')
      } catch (err) {
        toast.error(err instanceof Error ? err.message : 'Failed to add a document type.')
      }
    }
    await loadAssigned()
  }

  const handleRemove = async (id: number) => {
    setRemovingId(id)
    try {
      const res = await authFetch(apiUrl(`AlternateSignatories/delete_document_type/${id}`), { method: 'DELETE' })
      const data = await res.json().catch(() => null)
      if (!res.ok || !data?.success) throw new Error(data?.message ?? 'Failed to remove document type')
      setAssigned((prev) => prev.filter((a) => a.id !== id))
      toast.success('Document type removed.')
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to remove document type.')
    } finally {
      setRemovingId(null)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Document Types</DialogTitle>
          <DialogDescription>
            Document types {alternateName} is authorized to sign on your behalf.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-2">
          {loading ? (
            <div className="flex justify-center py-6">
              <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
            </div>
          ) : assigned.length === 0 ? (
            <p className="py-4 text-center text-sm text-muted-foreground">
              No document types assigned yet.
            </p>
          ) : (
            <div className="rounded-lg border divide-y">
              {assigned.map((a) => (
                <div key={a.id} className="flex items-center justify-between px-3 py-2">
                  <span className="flex items-center gap-1.5 text-sm">
                    <FileText className="h-3.5 w-3.5 text-muted-foreground" />
                    {a.documentDescription}
                    {a.documentAbbr && (
                      <Badge variant="outline" className="text-[10px]">{a.documentAbbr}</Badge>
                    )}
                  </span>
                  <Button
                    size="icon"
                    variant="ghost"
                    className="h-7 w-7 text-destructive hover:text-destructive"
                    disabled={removingId === a.id}
                    onClick={() => handleRemove(a.id)}
                  >
                    {removingId === a.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
                  </Button>
                </div>
              ))}
            </div>
          )}

          <DynamicMultiSelect
            api={apiUrl('References/get_document_types')}
            placeholder="Add a document type..."
            value={[]}
            showSelectAll={false}
            onChangeCallback={(ids) => handleAdd(ids)}
          />
        </div>
      </DialogContent>
    </Dialog>
  )
}
