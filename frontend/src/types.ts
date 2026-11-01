export type TermInfo = {
  termId: number
  code: string
  name: string
  startDate: string
  endDate: string
}

export type DraftInfo = {
  draftId: number
  termId: number
  name: string
  source: string
  status: string
  notes: string | null
  createdAt: string
  updatedAt: string
}

export type ValidationIssue = {
  code: string
  severity: string
  message: string
  contractId: number | null
  tenantCode: string | null
  apartmentId: number | null
  apartmentNumber: string | null
  relatedContractId: number | null
  relatedTenantCode: string | null
}

export type AssignedTenant = {
  contractId: number
  tenantId: number
  tenantCode: string
  firstName: string
  lastName: string
  assignmentId: number | null
  isLocked: boolean
  lockedBy: string | null
  lockedAt: string | null
  assignmentSource: string | null
}

export type ApartmentRow = {
  apartmentId: number
  apartmentNumber: string
  floorPlanCode: string
  floorPlanId: number
  building: string
  floor: number
  effectiveCapacity: number
  isActive: boolean
  occupants: AssignedTenant[]
}

export type UnassignedTenant = {
  contractId: number
  tenantId: number
  tenantCode: string
  firstName: string
  lastName: string
  floorPlanCode: string
  floorPlanId: number
  tenantType: string
}

export type RoommateRequest = {
  requestId: number
  contractId: number
  tenantCode: string
  requesterName: string
  rawText: string
  status: string
  exclusionReason: string | null
}

export type Workspace = {
  draft: DraftInfo
  term: TermInfo
  apartments: ApartmentRow[]
  unassigned: UnassignedTenant[]
  roommateRequests: RoommateRequest[]
  restrictions: unknown[]
  issues: ValidationIssue[]
  validationLimitations: string[]
}

export type MutationResult = {
  ok: boolean
  issues: ValidationIssue[]
  error?: string
  historyId?: number | null
}

export type HistoryEntry = {
  contractId: number
  tenantCode: string | null
  apartmentId: number | null
  apartmentNumber: string | null
  assigned: boolean
  isLocked: boolean
  lockedBy: string | null
  lockedAt: string | null
  source: string | null
}

export type HistoryItem = {
  historyId: number
  draftId: number
  termId: number
  changeType: string
  performedBy: string
  reason: string | null
  before: HistoryEntry[]
  after: HistoryEntry[]
  undoesHistoryId: number | null
  undoneByHistoryId: number | null
  isUndoable: boolean
  createdAt: string
}

export type AutoMatchSummary = {
  placed: number
  stillUnassigned: number
  mutualPairsPlaced: number
  oneWayJoinsPlaced: number
  skipped: number
  headline: string
}

export type AutoMatchResult = {
  ok: boolean
  draft?: DraftInfo | null
  placedCount: number
  stillUnassigned: number
  mutualPairsPlaced: number
  oneWayJoinsPlaced?: number
  summary?: AutoMatchSummary
  placements: unknown[]
  skips: { contractId: number; tenantCode: string; reason: string }[]
  notes: string[]
  issues: ValidationIssue[]
  error?: string
}
