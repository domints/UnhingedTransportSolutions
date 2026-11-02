/* Shared types mirroring the backend DTOs. */

export interface Client {
  id: number
  name: string
  contact: string | null
  telegramChatId: string | null
  notes: string | null
  createdAt: string
}

export interface City {
  code: string
  name: string
  active: boolean
}

export type TripStatus = 'planned' | 'completed' | 'cancelled'

export interface Trip {
  id: number
  title: string
  originCode: string | null
  destinationCode: string | null
  plannedDate: string | null
  status: TripStatus
  notes: string | null
  createdAt: string
  transportMode: string
  courierClientId: number | null
  courierName: string | null
}

export interface TripListItem extends Trip {
  shipmentCount: number
  stops: string[]
}

export interface TripTemplate {
  id: number
  title: string
  originCode: string | null
  destinationCode: string | null
  stops: string[]
  transportMode: string
  createdAt: string
}

export interface TrackEvent {
  type: string
  message: string
  createdAt: string
}

export interface TrackInfo {
  number: string
  status: string
  originCode: string | null
  destinationCode: string
  destinationName: string | null
  createdAt: string
  deliveredAt: string | null
  unlockRequired: boolean
  events: TrackEvent[]
  /* present only when unlocked */
  originName?: string | null
  senderName?: string
  recipientName?: string
  description?: string
  transportMode?: string
  notes?: string | null
  tripTitle?: string | null
  tripPlannedDate?: string | null
  telegramClaimUrl?: string | null
}

export interface ShipmentListItem {
  id: number
  trackingNumber: string
  status: string
  transportMode: string
  originCode: string | null
  destinationCode: string
  destinationName: string
  senderName: string
  recipientName: string
  description: string
  tripId: number | null
  tripTitle: string | null
  tripPlannedDate: string | null
  createdAt: string
}

export interface ShipmentDetail {
  id: number
  trackingNumber: string
  status: string
  senderId: number
  senderName: string
  recipientId: number
  recipientName: string
  originCode: string | null
  originName: string | null
  destinationCode: string
  destinationName: string
  description: string
  transportMode: string
  notes: string | null
  pin: string
  tripId: number | null
  tripTitle: string | null
  tripPlannedDate: string | null
  tripStatus: string | null
  createdAt: string
  updatedAt: string
  deliveredAt: string | null
  events: TrackEvent[]
}

export interface ShipmentUpsert {
  senderId: number
  recipientId: number
  originCode: string
  destinationCode: string
  description: string
  transportMode: string
  notes: string | null
}

export interface AuthState {
  authenticated: boolean
  isAdmin: boolean
  name: string | null
  clientId: number | null
  clientName: string | null
}

/** Reply of GET /api/admin/identity. */
export interface AdminIdentity {
  sub: string | null
  clientId: number | null
  clientName: string | null
  onboarded: boolean
}

/** Rows of the claim picker: clients not yet bound to any login. */
export interface UnclaimedCourier {
  id: number
  name: string
  contact: string | null
}

/* The eight grantable API-key permissions (names.* beyond these — keys.*
 * above all — can never be granted). */
export type ApiKeyPermission =
  | 'shipments.read'
  | 'shipments.write'
  | 'clients.read'
  | 'clients.write'
  | 'trips.read'
  | 'trips.write'
  | 'cities.read'
  | 'cities.write'

export interface ApiKeyRow {
  id: number
  name: string
  keyPrefix: string
  permissions: ApiKeyPermission[]
  createdAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

/** Reply of POST /api/admin/keys — the full key is here exactly once. */
export interface ApiKeyCreated {
  key: string
  id: number
  name: string
  keyPrefix: string
  permissions: ApiKeyPermission[]
  createdAt: string
}
