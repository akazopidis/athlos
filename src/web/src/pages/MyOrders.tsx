import { Link } from 'react-router'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Empty, Loading, Section } from '@/components/page'
import { formatMoney } from '../money.ts'
import { useMyOrders } from './useMyOrders.ts'

// The customer's orders across all coaches (design §8, "/me").
export function MyOrders() {
  const { orders, returned } = useMyOrders()

  return (
    <Section title="My orders">
      {returned?.status === 'Pending' && (
        <Alert>
          <AlertDescription>Payment received. Confirming your order…</AlertDescription>
        </Alert>
      )}
      {returned?.status === 'Paid' && (
        <Alert>
          <AlertDescription>Thank you! Your purchase of {returned.programTitle} is confirmed.</AlertDescription>
        </Alert>
      )}
      {orders.isPending ? (
        <Loading />
      ) : orders.data?.items.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Program</TableHead>
              <TableHead>Price</TableHead>
              <TableHead>Status</TableHead>
              <TableHead>Date</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {orders.data.items.map((o) => (
              <TableRow key={o.id}>
                <TableCell>
                  {o.programTitle}{' '}
                  <Link to={`/c/${o.coachSlug}`} className="text-muted-foreground underline-offset-4 hover:underline">
                    {o.coachSlug}
                  </Link>
                </TableCell>
                <TableCell>{formatMoney(o.amount, o.currency)}</TableCell>
                <TableCell>
                  <Badge variant={o.status === 'Paid' ? 'default' : 'secondary'}>{o.status}</Badge>
                </TableCell>
                <TableCell>{new Date(o.paidAt ?? o.createdAt).toLocaleDateString()}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <Empty>No orders yet.</Empty>
      )}
    </Section>
  )
}
