import { createFileRoute } from '@tanstack/react-router'
import ForSignatureAlternateQueueView from '@/View/dgsign/ForSignatureAlternateQueueView'

export const Route = createFileRoute('/_authenticated/signature/ForSignatureAlternate')({
  component: ForSignatureAlternateQueueView,
})
