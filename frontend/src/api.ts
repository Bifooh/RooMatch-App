import type { DraftInfo, MutationResult, TermInfo, Workspace } from './types'

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
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractId, apartmentId }),
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
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/swap`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractIdA, contractIdB }),
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
): Promise<MutationResult> {
  const res = await fetch(`/api/drafts/${draftId}/lock`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ contractId, locked, lockedBy }),
  })
  const body = await res.json().catch(() => ({ ok: false, issues: [] }))
  if (!res.ok && !body.issues) {
    return { ok: false, issues: [], error: body.error ?? `Lock failed (${res.status})` }
  }
  return body as MutationResult
}
