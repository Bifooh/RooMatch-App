import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  createDraft,
  fetchDrafts,
  fetchTerms,
  fetchWorkspace,
  moveTenant,
  setLock,
  swapTenants,
} from './api'
import type {
  DraftInfo,
  TermInfo,
  UnassignedTenant,
  ValidationIssue,
  Workspace,
} from './types'

type Selected =
  | { kind: 'assigned'; contractId: number; apartmentId: number }
  | { kind: 'unassigned'; contractId: number }
  | null

function displayName(first: string, last: string, code: string) {
  return `${first} ${last} (${code})`
}

function matchesQuery(
  q: string,
  fields: { name?: string; code?: string; apartment?: string; plan?: string },
) {
  if (!q.trim()) return true
  const n = q.trim().toLowerCase()
  return [fields.name, fields.code, fields.apartment, fields.plan]
    .filter(Boolean)
    .some((v) => v!.toLowerCase().includes(n))
}

export default function App() {
  const [terms, setTerms] = useState<TermInfo[]>([])
  const [termId, setTermId] = useState<number | ''>('')
  const [drafts, setDrafts] = useState<DraftInfo[]>([])
  const [draftId, setDraftId] = useState<number | ''>('')
  const [workspace, setWorkspace] = useState<Workspace | null>(null)
  const [operator, setOperator] = useState('Fictional Operator')
  const [query, setQuery] = useState('')
  const [planFilter, setPlanFilter] = useState('')
  const [selected, setSelected] = useState<Selected>(null)
  const [swapTarget, setSwapTarget] = useState<number | ''>('')
  const [moveTarget, setMoveTarget] = useState<number | ''>('')
  const [busy, setBusy] = useState(false)
  const [banner, setBanner] = useState<{ kind: 'ok' | 'bad' | 'info'; text: string } | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [issueExpanded, setIssueExpanded] = useState(false)
  const [showCreate, setShowCreate] = useState(false)
  const [newDraftName, setNewDraftName] = useState('')
  const [copyFrom, setCopyFrom] = useState<number | ''>('')
  const [copyLocks, setCopyLocks] = useState(true)
  const [showLimitations, setShowLimitations] = useState(false)
  const [showRequests, setShowRequests] = useState(false)

  useEffect(() => {
    fetchTerms()
      .then((t) => {
        setTerms(t)
        const winter = t.find((x) => x.code === 'W2027') ?? t[0]
        if (winter) setTermId(winter.termId)
      })
      .catch((e: Error) => setLoadError(e.message))
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    if (termId === '') return
    setDrafts([])
    setDraftId('')
    setWorkspace(null)
    fetchDrafts(termId)
      .then((d) => {
        setDrafts(d)
        if (d[0]) setDraftId(d[0].draftId)
      })
      .catch((e: Error) => setBanner({ kind: 'bad', text: e.message }))
  }, [termId])

  const reloadWorkspace = useCallback(async (id: number) => {
    const ws = await fetchWorkspace(id)
    setWorkspace(ws)
    setSelected(null)
    setSwapTarget('')
    setMoveTarget('')
  }, [])

  useEffect(() => {
    if (draftId === '') {
      setWorkspace(null)
      return
    }
    setBusy(true)
    reloadWorkspace(draftId)
      .catch((e: Error) => setBanner({ kind: 'bad', text: e.message }))
      .finally(() => setBusy(false))
  }, [draftId, reloadWorkspace])

  const plans = useMemo(() => {
    if (!workspace) return []
    return [...new Set(workspace.apartments.map((a) => a.floorPlanCode))].sort()
  }, [workspace])

  const filteredApartments = useMemo(() => {
    if (!workspace) return []
    return workspace.apartments.filter((a) => {
      if (planFilter && a.floorPlanCode !== planFilter) return false
      const aptHit = matchesQuery(query, {
        apartment: a.apartmentNumber,
        plan: a.floorPlanCode,
      })
      const occHit = a.occupants.some((o) =>
        matchesQuery(query, {
          name: `${o.firstName} ${o.lastName}`,
          code: o.tenantCode,
          apartment: a.apartmentNumber,
          plan: a.floorPlanCode,
        }),
      )
      return !query.trim() || aptHit || occHit
    })
  }, [workspace, query, planFilter])

  const filteredUnassigned = useMemo(() => {
    if (!workspace) return []
    return workspace.unassigned.filter((u) => {
      if (planFilter && u.floorPlanCode !== planFilter) return false
      return matchesQuery(query, {
        name: `${u.firstName} ${u.lastName}`,
        code: u.tenantCode,
        plan: u.floorPlanCode,
      })
    })
  }, [workspace, query, planFilter])

  const selectedLabel = useMemo(() => {
    if (!workspace || !selected) return null
    if (selected.kind === 'unassigned') {
      const u = workspace.unassigned.find((x) => x.contractId === selected.contractId)
      return u ? displayName(u.firstName, u.lastName, u.tenantCode) : null
    }
    for (const a of workspace.apartments) {
      const o = a.occupants.find((x) => x.contractId === selected.contractId)
      if (o) return `${displayName(o.firstName, o.lastName, o.tenantCode)} · apt ${a.apartmentNumber}`
    }
    return null
  }, [workspace, selected])

  const assignedOptions = useMemo(() => {
    if (!workspace || !selected || selected.kind !== 'assigned') return []
    return workspace.apartments
      .flatMap((a) =>
        a.occupants
          .filter((o) => o.contractId !== selected.contractId)
          .map((o) => ({
            contractId: o.contractId,
            label: `${displayName(o.firstName, o.lastName, o.tenantCode)} · ${a.apartmentNumber}`,
          })),
      )
      .sort((a, b) => a.label.localeCompare(b.label))
  }, [workspace, selected])

  const compatibleApartments = useMemo(() => {
    if (!workspace || !selected) return workspace?.apartments ?? []
    let planId: number | null = null
    if (selected.kind === 'unassigned') {
      planId =
        workspace.unassigned.find((u) => u.contractId === selected.contractId)?.floorPlanId ?? null
    } else {
      const apt = workspace.apartments.find((a) => a.apartmentId === selected.apartmentId)
      planId = apt?.floorPlanId ?? null
    }
    return workspace.apartments.filter((a) => planId == null || a.floorPlanId === planId)
  }, [workspace, selected])

  async function runMutation(action: () => Promise<{ ok: boolean; issues: ValidationIssue[]; error?: string }>) {
    if (draftId === '' || busy) return
    setBusy(true)
    setBanner(null)
    try {
      const result = await action()
      if (!result.ok) {
        const first = result.issues?.[0]?.message ?? result.error ?? 'Rejected'
        setBanner({ kind: 'bad', text: first })
      } else {
        const warn = result.issues?.find((i) => i.severity === 'warning')
        setBanner({
          kind: warn ? 'info' : 'ok',
          text: warn ? `Saved with warning: ${warn.message}` : 'Saved.',
        })
        await reloadWorkspace(draftId)
      }
    } catch (e) {
      setBanner({ kind: 'bad', text: e instanceof Error ? e.message : 'Request failed' })
    } finally {
      setBusy(false)
    }
  }

  async function onCreateDraft(e: React.FormEvent) {
    e.preventDefault()
    if (termId === '' || !newDraftName.trim() || busy) return
    setBusy(true)
    setBanner(null)
    try {
      const res = await createDraft(termId, {
        name: newDraftName.trim(),
        copyFromDraftId: copyFrom === '' ? null : copyFrom,
        copyLocks,
        notes: null,
      })
      if (!res.ok || !res.draft) {
        setBanner({ kind: 'bad', text: res.issues?.[0]?.message ?? 'Could not create draft' })
        return
      }
      const list = await fetchDrafts(termId)
      setDrafts(list)
      setDraftId(res.draft.draftId)
      setShowCreate(false)
      setNewDraftName('')
      setBanner({ kind: 'ok', text: `Created draft “${res.draft.name}”.` })
    } catch (err) {
      setBanner({ kind: 'bad', text: err instanceof Error ? err.message : 'Create failed' })
    } finally {
      setBusy(false)
    }
  }

  const errorIssues = workspace?.issues.filter((i) => i.severity === 'error') ?? []
  const warnIssues = workspace?.issues.filter((i) => i.severity === 'warning') ?? []
  const infoIssues = workspace?.issues.filter((i) => i.severity === 'info') ?? []
  const editable = workspace?.draft.status === 'draft'

  function selectUnassigned(u: UnassignedTenant) {
    setSelected({ kind: 'unassigned', contractId: u.contractId })
    setSwapTarget('')
    setMoveTarget('')
  }

  return (
    <main className="app">
      <header className="top">
        <div>
          <h1>Roomatch assignment workspace</h1>
          <p className="lede">
            Manual apartment assignment for fictional Lodge data. Backend validation is authoritative.
            Local only — not ready for confidential student data.
          </p>
        </div>
        <label className="operator">
          Operator identity
          <input
            value={operator}
            onChange={(e) => setOperator(e.target.value)}
            placeholder="Fictional staff name"
            aria-label="Fictional operator identity for locks"
          />
        </label>
      </header>

      {loading && <p role="status">Loading terms…</p>}
      {loadError && <p className="banner bad" role="alert">Could not load API: {loadError}</p>}
      {banner && (
        <p className={`banner ${banner.kind}`} role="status">
          {banner.text}
        </p>
      )}

      <section className="controls" aria-label="Term and draft">
        <label>
          Term
          <select
            value={termId}
            onChange={(e) => setTermId(e.target.value ? Number(e.target.value) : '')}
            disabled={busy}
          >
            <option value="">Select term…</option>
            {terms.map((t) => (
              <option key={t.termId} value={t.termId}>
                {t.code} — {t.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Draft
          <select
            value={draftId}
            onChange={(e) => setDraftId(e.target.value ? Number(e.target.value) : '')}
            disabled={busy || termId === ''}
          >
            <option value="">Select draft…</option>
            {drafts.map((d) => (
              <option key={d.draftId} value={d.draftId}>
                #{d.draftId} {d.name} ({d.status})
              </option>
            ))}
          </select>
        </label>
        <button type="button" onClick={() => setShowCreate((v) => !v)} disabled={termId === '' || busy}>
          {showCreate ? 'Cancel new draft' : 'New working draft'}
        </button>
        <button
          type="button"
          onClick={() => draftId !== '' && reloadWorkspace(draftId)}
          disabled={draftId === '' || busy}
        >
          Refresh
        </button>
      </section>

      {showCreate && (
        <form className="create-draft" onSubmit={onCreateDraft}>
          <h2>Create working draft</h2>
          <p>Copies create a new draft without changing the original. Unassigned tenants still come from term contracts.</p>
          <label>
            Name
            <input
              value={newDraftName}
              onChange={(e) => setNewDraftName(e.target.value)}
              required
              maxLength={100}
            />
          </label>
          <label>
            Copy assignments from
            <select
              value={copyFrom}
              onChange={(e) => setCopyFrom(e.target.value ? Number(e.target.value) : '')}
            >
              <option value="">Empty draft</option>
              {drafts.map((d) => (
                <option key={d.draftId} value={d.draftId}>
                  #{d.draftId} {d.name}
                </option>
              ))}
            </select>
          </label>
          <label className="check">
            <input
              type="checkbox"
              checked={copyLocks}
              onChange={(e) => setCopyLocks(e.target.checked)}
              disabled={copyFrom === ''}
            />
            Copy locks
          </label>
          <button type="submit" disabled={busy || !newDraftName.trim()}>
            Create
          </button>
        </form>
      )}

      {workspace && (
        <>
          <section className="meta" aria-label="Draft summary">
            <p>
              <strong>{workspace.draft.name}</strong> · {workspace.term.code} · status{' '}
              <code>{workspace.draft.status}</code>
              {!editable && ' · read-only (final/archived)'}
            </p>
            <p>
              Assigned {workspace.apartments.reduce((n, a) => n + a.occupants.length, 0)} · Unassigned{' '}
              {workspace.unassigned.length} · Active apartments {workspace.apartments.length}
            </p>
          </section>

          <section className="issues" aria-label="Validation issues">
            <div className="issues-bar">
              <strong>Validation</strong>
              <span>
                {errorIssues.length} errors · {warnIssues.length} warnings · {infoIssues.length} info
              </span>
              <button type="button" onClick={() => setIssueExpanded((v) => !v)}>
                {issueExpanded ? 'Hide details' : 'Show details'}
              </button>
              <button type="button" onClick={() => setShowLimitations((v) => !v)}>
                Limitations
              </button>
              <button type="button" onClick={() => setShowRequests((v) => !v)}>
                Roommate requests ({workspace.roommateRequests.length})
              </button>
            </div>
            {showLimitations && (
              <ul className="limitations">
                {workspace.validationLimitations.map((l) => (
                  <li key={l}>{l}</li>
                ))}
              </ul>
            )}
            {issueExpanded && (
              <ul className="issue-list">
                {[...errorIssues, ...warnIssues, ...infoIssues].map((i, idx) => (
                  <li key={`${i.code}-${idx}`} className={i.severity}>
                    <code>{i.code}</code> {i.message}
                  </li>
                ))}
                {workspace.issues.length === 0 && <li>No issues in current draft state.</li>}
              </ul>
            )}
            {showRequests && (
              <div className="requests">
                <h3>Roommate requests (original wording preserved)</h3>
                <table>
                  <thead>
                    <tr>
                      <th>Requester</th>
                      <th>Wording</th>
                      <th>Status</th>
                      <th>Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {workspace.roommateRequests.map((r) => (
                      <tr key={r.requestId}>
                        <td>
                          {r.requesterName}
                          <br />
                          <code>{r.tenantCode}</code>
                        </td>
                        <td>{r.rawText}</td>
                        <td>{r.status}</td>
                        <td>{r.exclusionReason ?? '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="filters" aria-label="Search and filters">
            <label>
              Search
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Name, tenant code, apartment, plan"
              />
            </label>
            <label>
              Floor plan
              <select value={planFilter} onChange={(e) => setPlanFilter(e.target.value)}>
                <option value="">All plans</option>
                {plans.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </select>
            </label>
          </section>

          <section className="action-panel" aria-label="Selected tenant actions">
            <h2>Selection</h2>
            {!selected && <p>Select a tenant from an apartment or the unassigned list.</p>}
            {selected && (
              <>
                <p>
                  Selected: <strong>{selectedLabel}</strong>
                </p>
                <div className="action-row">
                  <label>
                    Move to apartment
                    <select
                      value={moveTarget}
                      onChange={(e) => setMoveTarget(e.target.value ? Number(e.target.value) : '')}
                      disabled={!editable || busy}
                    >
                      <option value="">Choose…</option>
                      {compatibleApartments.map((a) => (
                        <option key={a.apartmentId} value={a.apartmentId}>
                          {a.apartmentNumber} · {a.floorPlanCode} · fl {a.floor} ·{' '}
                          {a.occupants.length}/{a.effectiveCapacity}
                        </option>
                      ))}
                    </select>
                  </label>
                  <button
                    type="button"
                    disabled={!editable || busy || moveTarget === ''}
                    onClick={() =>
                      runMutation(() =>
                        moveTenant(draftId as number, selected.contractId, moveTarget as number),
                      )
                    }
                  >
                    Move
                  </button>
                  <button
                    type="button"
                    disabled={!editable || busy || selected.kind !== 'assigned'}
                    onClick={() =>
                      runMutation(() => moveTenant(draftId as number, selected.contractId, null))
                    }
                  >
                    Unassign
                  </button>
                </div>
                {selected.kind === 'assigned' && (
                  <div className="action-row">
                    <label>
                      Swap with
                      <select
                        value={swapTarget}
                        onChange={(e) => setSwapTarget(e.target.value ? Number(e.target.value) : '')}
                        disabled={!editable || busy}
                      >
                        <option value="">Choose assigned tenant…</option>
                        {assignedOptions.map((o) => (
                          <option key={o.contractId} value={o.contractId}>
                            {o.label}
                          </option>
                        ))}
                      </select>
                    </label>
                    <button
                      type="button"
                      disabled={!editable || busy || swapTarget === ''}
                      onClick={() =>
                        runMutation(() =>
                          swapTenants(draftId as number, selected.contractId, swapTarget as number),
                        )
                      }
                    >
                      Swap
                    </button>
                    <button
                      type="button"
                      disabled={!editable || busy || !operator.trim()}
                      onClick={() => {
                        const apt = workspace.apartments.find(
                          (a) => a.apartmentId === selected.apartmentId,
                        )
                        const occ = apt?.occupants.find((o) => o.contractId === selected.contractId)
                        const locked = !occ?.isLocked
                        return runMutation(() =>
                          setLock(draftId as number, selected.contractId, locked, operator.trim()),
                        )
                      }}
                    >
                      Toggle lock
                    </button>
                  </div>
                )}
              </>
            )}
          </section>

          <div className="workspace-grid">
            <section aria-label="Apartments">
              <h2>Apartments</h2>
              {filteredApartments.length === 0 && <p className="empty">No apartments match.</p>}
              <ul className="apt-list">
                {filteredApartments.map((a) => (
                  <li key={a.apartmentId} className="apt-card">
                    <div className="apt-head">
                      <strong>{a.apartmentNumber}</strong>
                      <span>
                        {a.floorPlanCode} · {a.building} fl {a.floor}
                      </span>
                      <span>
                        {a.occupants.length}/{a.effectiveCapacity}
                      </span>
                    </div>
                    <ul className="occ-list">
                      {a.occupants.length === 0 && <li className="empty">Empty</li>}
                      {a.occupants.map((o) => (
                        <li key={o.contractId}>
                          <button
                            type="button"
                            className={
                              selected?.contractId === o.contractId ? 'tenant selected' : 'tenant'
                            }
                            onClick={() =>
                              setSelected({
                                kind: 'assigned',
                                contractId: o.contractId,
                                apartmentId: a.apartmentId,
                              })
                            }
                          >
                            {displayName(o.firstName, o.lastName, o.tenantCode)}
                            {o.isLocked ? ` [locked by ${o.lockedBy}]` : ''}
                          </button>
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            </section>

            <section aria-label="Unassigned tenants">
              <h2>Unassigned ({filteredUnassigned.length})</h2>
              {filteredUnassigned.length === 0 && <p className="empty">None match filters.</p>}
              <ul className="unassigned-list">
                {filteredUnassigned.map((u) => (
                  <li key={u.contractId}>
                    <button
                      type="button"
                      className={
                        selected?.contractId === u.contractId ? 'tenant selected' : 'tenant'
                      }
                      onClick={() => selectUnassigned(u)}
                    >
                      {displayName(u.firstName, u.lastName, u.tenantCode)}
                      <span className="plan">{u.floorPlanCode}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          </div>
        </>
      )}

      {!workspace && !loading && !loadError && draftId === '' && (
        <p className="empty">Select a term and draft to begin.</p>
      )}
      {busy && <p className="busy" role="status">Working…</p>}
    </main>
  )
}
