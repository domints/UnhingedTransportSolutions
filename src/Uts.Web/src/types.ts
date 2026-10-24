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
}

export interface TripListItem extends Trip {
  shipmentCount: number
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
}
