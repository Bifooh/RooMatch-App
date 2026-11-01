import type {
  DraftInfo,
  HistoryItem,
  MutationResult,
  RoommateMutationResult,
  RoommatePrepPayload,
  TermInfo,
  Workspace,
} from './types'

async function parseJson<T>(res: Response): Promise<T> {
  const body = await res.json().catch(() => null)
  if (!res.ok) {
    const msg =
      (body && (body.error || body.message)) ||
      (body?.issues?.[0]?.message) ||
      `${res.status} ${res.statusText}`
    const err = new Error(typeof msg === 'string' ? msg : 'Request failed') as Error & {
      status?: number
      body?: unknown
    }
    err.status = res.status
    err.body = body
    throw err
  }
  return body as T
}

export async function fetchTerms(): Promise<TermInfo[]> {
  return parseJson(await fetch('/api/terms'))
}

export async function fetchDrafts(termId: number): Promise<DraftInfo[]> {
  return parseJson(await fetch(`/api/terms/${termId}/drafts`))
}

export async function createDraft(
  termId: number,
  body: { name: string; copyFromDraftId: number | null; copyLocks: boolean; notes: string | null },
): Promise<{ ok: boolean; draft?: DraftInfo; issues: MutationResult['issues'] }> {
  return parseJson(
    await fetch(`/api/terms/${termId}/drafts`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),
  )
}

export async function fetchWorkspace(draftId: number): Promise<Workspace> {
  return parseJson(await fetch(`/api/drafts/${draftId}/workspace`))
}

export async function moveTenant(
  draftId: number,
  contractId: number,
  apartmentId: number | null,
  performedBy?: string,
  reason?: string | null,
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractId, apartmentId, performedBy, reason: reason || null }),
  })
  const body = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !body.issues) {
    return { ok: false, issues: [], error: body.error ?? `Move failed (${res.status})` }
  }
  return body as MutationResult
}

export async function swapTenants(
  draftId: number,
  contractIdA: number,
  contractIdB: number,
  performedBy?: string,
  reason?: string | null,
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/swap`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractIdA, contractIdB, performedBy, reason: reason || null }),
  })
  const body = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !body.issues) {
    return { ok: false, issues: [], error: body.error ?? `Swap failed (${res.status})` }
  }
  return body as MutationResult
}

export async function setLock(
  draftId: number,
  contractId: number,
  locked: boolean,
  lockedBy: string,
  reason?: string | null,
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/lock`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractId, locked, lockedBy, reason: reason || null }),
  })
  const body = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !body.issues) {
    return { ok: false, issues: [], error: body.error ?? `Lock failed (${res.status})` }
  }
  return body as MutationResult
}

export async function fetchHistory(draftId: number, limit = 50): Promise<HistoryItem[]> {
  return parseJson(await fetch(`/api/drafts/${draftId}/history?limit=${limit}`))
}

export async function undoChange(
  draftId: number,
  body: { historyId: number | null; performedBy: string; reason: string | null },
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/undo`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsed = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !parsed.issues) {
    return { ok: false, issues: [], error: parsed.error ?? `Undo failed (${res.status})` }
  }
  return parsed as MutationResult
}

export async function fetchRoommatePrep(termId: number): Promise<RoommatePrepPayload> {
  return parseJson(await fetch(`/api/terms/${termId}/roommate-requests/prep`))
}

export async function resolveRoommateRequest(
  termId: number,
  requestId: number,
  body: { resolvedTenantId: number; performedBy: string; note: string | null },
): Promise<RoommateMutationResult> {
  const res = await fetch(`/api/terms/${termId}/roommate-requests/${requestId}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsed = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !parsed.issues) {
    return { ok: false, issues: [], error: parsed.error ?? `Resolve failed (${res.status})` }
  }
  return parsed as RoommateMutationResult
}

export async function markRoommateRequest(
  termId: number,
  requestId: number,
  body: { status: string; exclusionReason: string; performedBy: string; note: string | null },
): Promise<RoommateMutationResult> {
  const res = await fetch(`/api/terms/${termId}/roommate-requests/${requestId}/mark`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsed = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !parsed.issues) {
    return { ok: false, issues: [], error: parsed.error ?? `Mark failed (${res.status})` }
  }
  return parsed as RoommateMutationResult
}
