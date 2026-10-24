/* Status labels, allowed transitions and date formatting — mirrors ShipmentStatus.cs */

export const STATUS_LABELS: Record<string, string> = {
  registered: 'Registered',
  picked_up: 'Picked up',
  in_transit: 'In transit',
  delivered: 'Delivered',
  cancelled: 'Cancelled',
  lost: 'Lost',
}

export const TRANSITIONS: Record<string, string[]> = {
  registered: ['picked_up', 'cancelled'],
  picked_up: ['in_transit', 'cancelled', 'lost'],
  in_transit: ['delivered', 'lost'],
  delivered: [],
  cancelled: [],
  lost: [],
}

export const MODE_LABELS: Record<string, string> = {
  train: 'Train-friendly',
  car: 'Car required',
  unknown: 'Mode unknown',
}

export const TRIP_STATUS_LABELS: Record<string, string> = {
  planned: 'Planned',
  completed: 'Completed',
  cancelled: 'Cancelled',
}

export function statusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status
}

export function modeLabel(mode: string): string {
  return MODE_LABELS[mode] ?? mode
}

export function tripStatusLabel(status: string): string {
  return TRIP_STATUS_LABELS[status] ?? status
}

export function fmtDate(iso?: string | null): string {
  if (!iso) return '—'
  return new Date(iso.length === 10 || (iso.length === 11 && iso.endsWith('Z')) ? `${iso}T00:00:00` : iso).toLocaleDateString('en-GB', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
  })
}

export function fmtDateTime(iso?: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('en-GB', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}
