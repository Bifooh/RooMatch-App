import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  createDraft,
  fetchDrafts,
  fetchHistory,
  fetchRoommatePrep,
  fetchTerms,
  fetchWorkspace,
  markRoommateRequest,
  moveTenant,
  resolveRoommateRequest,
  runAutoMatch,
  setLock,
  swapTenants,
  undoChange,
} from './api'
import type {
  DraftInfo,
  HistoryEntry,
  HistoryItem,
  RoommatePrepItem,
  RoommatePrepPayload,
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

function formatWhen(iso: string) {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

function describeEntry(
  e: HistoryEntry,
  nameByCode: Map<string, string>,
): string {
  const label = e.tenantCode
    ? nameByCode.get(e.tenantCode) ?? e.tenantCode
    : `contract ${e.contractId}`
  const code = e.tenantCode ? ` (${e.tenantCode})` : ''
  if (!e.assigned) return `${label}${code} → unassigned`
  const apt = e.apartmentNumber ?? (e.apartmentId != null ? `apt#${e.apartmentId}` : '?')
  const lock = e.isLocked ? ` [locked${e.lockedBy ? ` by ${e.lockedBy}` : ''}]` : ''
  return `${label}${code} → ${apt}${lock}`
}

function summarizeChange(item: HistoryItem, nameByCode: Map<string, string>) {
  const after = item.after.map((e) => describeEntry(e, nameByCode)).join('; ')
  return after || item.changeType
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
  const [history, setHistory] = useState<HistoryItem[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [changeReason, setChangeReason] = useState('')
  const [matching, setMatching] = useState(false)
  const [matchSummary, setMatchSummary] = useState<string | null>(null)
  const [historyExpanded, setHistoryExpanded] = useState(false)
  const [showPrep, setShowPrep] = useState(false)
  const [showTools, setShowTools] = useState(false)
  const [prep, setPrep] = useState<RoommatePrepPayload | null>(null)
  const [prepLoading, setPrepLoading] = useState(false)
  const [prepError, setPrepError] = useState<string | null>(null)
  const [prepFilter, setPrepFilter] = useState<'all' | 'missing' | 'duplicate' | 'nearOnly' | 'exact'>('all')
  const [prepReasons, setPrepReasons] = useState<Record<number, string>>({})
  const [prepPicks, setPrepPicks] = useState<Record<number, number | ''>>({})

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

  const reloadPrep = useCallback(async (id: number) => {
    setPrepLoading(true)
    setPrepError(null)
    try {
      const payload = await fetchRoommatePrep(id)
      setPrep(payload)
      const picks: Record<number, number | ''> = {}
      for (const item of payload.items) {
        if (item.conflictKind === 'exact' && item.candidates.length === 1) {
          picks[item.requestId] = item.candidates[0].tenantId
        } else {
          picks[item.requestId] = ''
        }
      }
      setPrepPicks(picks)
    } catch (e) {
      setPrep(null)
      setPrepError(e instanceof Error ? e.message : 'Could not load roommate prep')
    } finally {
      setPrepLoading(false)
    }
  }, [])

  useEffect(() => {
    if (termId === '') return
    setDrafts([])
    setDraftId('')
    setWorkspace(null)
    setPrep(null)
    fetchDrafts(termId)
      .then((d) => {
        setDrafts(d)
        if (d[0]) setDraftId(d[0].draftId)
      })
      .catch((e: Error) => setBanner({ kind: 'bad', text: e.message }))
    void reloadPrep(termId)
  }, [termId, reloadPrep])

  const reloadHistory = useCallback(async (id: number) => {
    setHistoryLoading(true)
    setHistoryError(null)
    try {
      setHistory(await fetchHistory(id))
    } catch (e) {
      setHistory([])
      setHistoryError(e instanceof Error ? e.message : 'Could not load history')
    } finally {
      setHistoryLoading(false)
    }
  }, [])

  const reloadWorkspace = useCallback(async (id: number) => {
    const ws = await fetchWorkspace(id)
    setWorkspace(ws)
    setSelected(null)
    setSwapTarget('')
    setMoveTarget('')
    await reloadHistory(id)
  }, [reloadHistory])

  useEffect(() => {
    if (draftId === '') {
      setWorkspace(null)
      setHistory([])
      setHistoryError(null)
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

  const nameByCode = useMemo(() => {
    const map = new Map<string, string>()
    if (!workspace) return map
    for (const a of workspace.apartments) {
      for (const o of a.occupants) {
        map.set(o.tenantCode, `${o.firstName} ${o.lastName}`)
      }
    }
    for (const u of workspace.unassigned) {
      map.set(u.tenantCode, `${u.firstName} ${u.lastName}`)
    }
    return map
  }, [workspace])

  const lastUndoable = useMemo(
    () => history.find((h) => h.isUndoable) ?? null,
    [history],
  )

  async function runMutation(action: () => Promise<{ ok: boolean; issues: ValidationIssue[]; error?: string }>) {
    if (draftId === '' || busy) return
    setBusy(true)
    setBanner(null)
    try {
      const result = await action()
      if (!result.ok) {
        const detail = result.issues?.length
          ? result.issues.map((i) => `${i.code}: ${i.message}`).join(' · ')
          : (result.error ?? 'Rejected')
        setBanner({ kind: 'bad', text: detail })
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

  async function onUndo(historyId: number | null) {
    if (draftId === '' || busy || !operator.trim()) return
    await runMutation(() =>
      undoChange(draftId, {
        historyId,
        performedBy: operator.trim(),
        reason: changeReason.trim() || null,
      }),
    )
  }

  async function runPrepMutation(
    action: () => Promise<{ ok: boolean; issues: ValidationIssue[]; error?: string }>,
  ) {
    if (termId === '' || busy || !operator.trim()) return
    setBusy(true)
    setBanner(null)
    try {
      const result = await action()
      if (!result.ok) {
        const detail = result.issues?.length
          ? result.issues.map((i) => `${i.code}: ${i.message}`).join(' · ')
          : (result.error ?? 'Rejected')
        setBanner({ kind: 'bad', text: detail })
      } else {
        setBanner({ kind: 'ok', text: 'Roommate request updated.' })
        await reloadPrep(termId)
        if (draftId !== '') await reloadWorkspace(draftId)
      }
    } catch (e) {
      setBanner({ kind: 'bad', text: e instanceof Error ? e.message : 'Request failed' })
    } finally {
      setBusy(false)
    }
  }

  function onResolvePrep(item: RoommatePrepItem) {
    if (termId === '') return
    const pick = prepPicks[item.requestId]
    if (pick === '' || pick == null) {
      setBanner({ kind: 'bad', text: 'Pick a candidate before confirming.' })
      return
    }
    void runPrepMutation(() =>
      resolveRoommateRequest(termId, item.requestId, {
        resolvedTenantId: pick,
        performedBy: operator.trim(),
        note: changeReason.trim() || null,
      }),
    )
  }

  function onMarkPrep(item: RoommatePrepItem, status: 'unresolved' | 'ambiguous' | 'excluded') {
    if (termId === '') return
    const reason =
      (prepReasons[item.requestId] ?? '').trim() ||
      (status === 'unresolved'
        ? 'Person not found in this term’s tenant list.'
        : status === 'ambiguous'
          ? 'Multiple people could match; left for later matching to ignore or soft-match.'
          : 'Excluded from matching.')
    void runPrepMutation(() =>
      markRoommateRequest(termId, item.requestId, {
        status,
        exclusionReason: reason,
        performedBy: operator.trim(),
        note: changeReason.trim() || null,
      }),
    )
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

  async function onGenerateMatch() {
    if (termId === '' || busy || matching || !operator.trim()) return
    setMatching(true)
    setBusy(true)
    setBanner(null)
    setMatchSummary(null)
    try {
      const res = await runAutoMatch(termId, {
        draftName: null,
        copyFromDraftId: draftId === '' ? null : draftId,
        copyLocks: true,
        performedBy: operator.trim(),
        notes: 'auto-match v1',
      })
      if (!res.ok || !res.draft) {
        const detail = res.issues?.length
          ? res.issues.map((i) => `${i.code}: ${i.message}`).join(' · ')
          : (res.error ?? res.summary?.headline ?? 'Match failed')
        setBanner({ kind: 'bad', text: detail })
        return
      }
      const list = await fetchDrafts(termId)
      setDrafts(list)
      setDraftId(res.draft.draftId)
      const headline =
        res.summary?.headline ??
        `Plan ready: ${res.placedCount} placed, ${res.stillUnassigned} still need a spot (mutual ${res.mutualPairsPlaced}, one-way ${res.oneWayJoinsPlaced ?? 0}).`
      setMatchSummary(headline)
      setBanner({ kind: 'ok', text: headline })
    } catch (err) {
      setBanner({ kind: 'bad', text: err instanceof Error ? err.message : 'Match failed' })
    } finally {
      setMatching(false)
      setBusy(false)
    }
  }

  const errorIssues = workspace?.issues.filter((i) => i.severity === 'error') ?? []
  const warnIssues = workspace?.issues.filter((i) => i.severity === 'warning') ?? []
  const editable = workspace?.draft.status === 'draft'

  const prepItems = useMemo(() => {
    if (!prep) return []
    if (prepFilter === 'all') return prep.items
    return prep.items.filter((i) => i.conflictKind === prepFilter)
  }, [prep, prepFilter])


  function aptFillClass(occ: number, cap: number) {
    if (cap <= 0) return 'fill-mid'
    const r = occ / cap
    if (r >= 1) return 'fill-full'
    if (r >= 0.5) return 'fill-mid'
    return 'fill-low'
  }

  function buildingClass(building: string) {
    const b = building.toLowerCase()
    if (b.includes('women')) return 'building-women'
    if (b.includes('men')) return 'building-men'
    return ''
  }

  function selectUnassigned(u: UnassignedTenant) {
    setSelected({ kind: 'unassigned', contractId: u.contractId })
    setSwapTarget('')
    setMoveTarget('')
  }

  return (
    <main className="app">
      <header className="hero">
        <h1 className="hero-brand">Roomatch</h1>
        <p className="hero-lede">
          Pick a term, make a first plan automatically, then tweak apartments as boxes of students.
          Fictional Lodge data only — not for real student records.
        </p>
        <div className="hero-actions">
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
          <button
            type="button"
            className="btn-primary"
            onClick={() => void onGenerateMatch()}
            disabled={termId === '' || busy || matching || !operator.trim()}
            aria-busy={matching}
          >
            {matching ? 'Making plan…' : 'Make first plan'}
          </button>
        </div>
        <p className="hero-hint">
          Places people by floor plan and resolved roommate pairs. You can move anyone afterward.
        </p>
      </header>

      {loading && <p role="status">Loading…</p>}
      {loadError && (
        <p className="banner bad" role="alert">
          Could not reach the API: {loadError}
        </p>
      )}
      {banner && (
        <p className={`banner ${banner.kind}`} role="status">
          {banner.text}
        </p>
      )}
      {matchSummary && (
        <p className="banner info" role="status">
          Last generate: {matchSummary}
        </p>
      )}

      {termId !== '' && (
        <section className="plan-strip" aria-label="Working plan">
          <label>
            Working plan
            <select
              value={draftId}
              onChange={(e) => setDraftId(e.target.value ? Number(e.target.value) : '')}
              disabled={busy}
            >
              <option value="">None yet — make a first plan</option>
              {drafts.map((d) => (
                <option key={d.draftId} value={d.draftId}>
                  {d.name} {d.status !== 'draft' ? `(${d.status})` : ''}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            onClick={() => draftId !== '' && reloadWorkspace(draftId)}
            disabled={draftId === '' || busy}
          >
            Refresh
          </button>
          {workspace && (
            <p className="plan-meta">
              {workspace.apartments.reduce((n, a) => n + a.occupants.length, 0)} placed ·{' '}
              {workspace.unassigned.length} unassigned
              {!editable ? ' · read-only' : ''}
            </p>
          )}
        </section>
      )}

      {workspace && (
        <>
          <div className="board-head">
            <h2>Apartments</h2>
            <div className="board-filters">
              <label>
                Find
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Name or apartment"
                />
              </label>
              <label>
                Floor plan
                <select value={planFilter} onChange={(e) => setPlanFilter(e.target.value)}>
                  <option value="">All</option>
                  {plans.map((p) => (
                    <option key={p} value={p}>
                      {p}
                    </option>
                  ))}
                </select>
              </label>
            </div>
          </div>

          <div className="workspace-layout">
            <section aria-label="Apartment boxes">
              {filteredApartments.length === 0 && <p className="empty">No apartments match.</p>}
              <ul className="apt-board">
                {filteredApartments.map((a) => {
                  const fill = aptFillClass(a.occupants.length, a.effectiveCapacity)
                  const bldg = buildingClass(a.building)
                  return (
                    <li
                      key={a.apartmentId}
                      className={`apt-box ${bldg} ${fill}`}
                    >
                      <div className="apt-box-head">
                        <strong>{a.apartmentNumber}</strong>
                        <span className="apt-box-meta">
                          {a.occupants.length}/{a.effectiveCapacity}
                        </span>
                      </div>
                      <div className="apt-box-meta">
                        {a.floorPlanCode} · fl {a.floor}
                      </div>
                      <ul className="occ-chips">
                        {a.occupants.length === 0 && (
                          <li className="apt-empty">Empty</li>
                        )}
                        {a.occupants.map((o) => (
                          <li key={o.contractId}>
                            <button
                              type="button"
                              className={
                                selected?.contractId === o.contractId ? 'chip selected' : 'chip'
                              }
                              onClick={() =>
                                setSelected({
                                  kind: 'assigned',
                                  contractId: o.contractId,
                                  apartmentId: a.apartmentId,
                                })
                              }
                            >
                              {o.firstName} {o.lastName}
                              {o.isLocked ? <span className="lock-mark"> · locked</span> : null}
                            </button>
                          </li>
                        ))}
                      </ul>
                    </li>
                  )
                })}
              </ul>
            </section>

            <aside className="side-col">
              <section className="panel" aria-label="Adjust selection">
                <h2>Adjust</h2>
                {!selected && (
                  <p className="muted">Click a student in a box, or someone waiting below.</p>
                )}
                {selected && (
                  <>
                    <p>
                      <strong>{selectedLabel}</strong>
                    </p>
                    <div className="action-row">
                      <label>
                        Move to
                        <select
                          value={moveTarget}
                          onChange={(e) =>
                            setMoveTarget(e.target.value ? Number(e.target.value) : '')
                          }
                          disabled={!editable || busy}
                        >
                          <option value="">Choose…</option>
                          {compatibleApartments.map((a) => (
                            <option key={a.apartmentId} value={a.apartmentId}>
                              {a.apartmentNumber} · {a.occupants.length}/{a.effectiveCapacity}
                            </option>
                          ))}
                        </select>
                      </label>
                      <button
                        type="button"
                        disabled={!editable || busy || moveTarget === '' || !operator.trim()}
                        onClick={() =>
                          runMutation(() =>
                            moveTenant(
                              draftId as number,
                              selected.contractId,
                              moveTarget as number,
                              operator.trim(),
                              changeReason.trim() || null,
                            ),
                          )
                        }
                      >
                        Move
                      </button>
                      <button
                        type="button"
                        disabled={
                          !editable || busy || selected.kind !== 'assigned' || !operator.trim()
                        }
                        onClick={() =>
                          runMutation(() =>
                            moveTenant(
                              draftId as number,
                              selected.contractId,
                              null,
                              operator.trim(),
                              changeReason.trim() || null,
                            ),
                          )
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
                            onChange={(e) =>
                              setSwapTarget(e.target.value ? Number(e.target.value) : '')
                            }
                            disabled={!editable || busy}
                          >
                            <option value="">Choose…</option>
                            {assignedOptions.map((o) => (
                              <option key={o.contractId} value={o.contractId}>
                                {o.label}
                              </option>
                            ))}
                          </select>
                        </label>
                        <button
                          type="button"
                          disabled={!editable || busy || swapTarget === '' || !operator.trim()}
                          onClick={() =>
                            runMutation(() =>
                              swapTenants(
                                draftId as number,
                                selected.contractId,
                                swapTarget as number,
                                operator.trim(),
                                changeReason.trim() || null,
                              ),
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
                            const occ = apt?.occupants.find(
                              (o) => o.contractId === selected.contractId,
                            )
                            const locked = !occ?.isLocked
                            return runMutation(() =>
                              setLock(
                                draftId as number,
                                selected.contractId,
                                locked,
                                operator.trim(),
                                changeReason.trim() || null,
                              ),
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

              <section className="panel" aria-label="Waiting list">
                <h2>Waiting ({filteredUnassigned.length})</h2>
                {filteredUnassigned.length === 0 && (
                  <p className="empty">Everyone on this plan has a spot (or filters hide them).</p>
                )}
                <ul className="unassigned-list">
                  {filteredUnassigned.map((u) => (
                    <li key={u.contractId}>
                      <button
                        type="button"
                        className={
                          selected?.contractId === u.contractId ? 'chip selected' : 'chip'
                        }
                        onClick={() => selectUnassigned(u)}
                      >
                        {u.firstName} {u.lastName}
                        <span className="muted"> · {u.floorPlanCode}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              </section>

              {(errorIssues.length > 0 || warnIssues.length > 0) && (
                <section className="panel" aria-label="Alerts">
                  <h3>Needs attention</h3>
                  <p className="muted">
                    {errorIssues.length} blocks · {warnIssues.length} warnings
                  </p>
                  <button type="button" onClick={() => setIssueExpanded((v) => !v)}>
                    {issueExpanded ? 'Hide details' : 'Show details'}
                  </button>
                  {issueExpanded && (
                    <ul className="issue-list">
                      {[...errorIssues, ...warnIssues].map((i, idx) => (
                        <li key={`${i.code}-${idx}`} className={i.severity}>
                          <code>{i.code}</code> {i.message}
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              )}
            </aside>
          </div>
        </>
      )}

      {!workspace && !loading && !loadError && termId !== '' && (
        <p className="empty">Make a first plan for this term, or pick an existing working plan above.</p>
      )}

      <section className="tools">
        <button
          type="button"
          className="tools-toggle"
          onClick={() => setShowTools((v) => !v)}
        >
          {showTools ? 'Hide advanced tools' : 'Advanced tools (roommate names, history, copy…)'}
        </button>
        {showTools && (
          <div className="tools-body">
            <div className="operator-block panel">
              <label className="operator">
                Your name (for locks &amp; undo)
                <input
                  value={operator}
                  onChange={(e) => setOperator(e.target.value)}
                  placeholder="Fictional staff name"
                />
              </label>
              <label className="operator">
                Note (optional)
                <input
                  value={changeReason}
                  onChange={(e) => setChangeReason(e.target.value)}
                  placeholder="Not required"
                  maxLength={255}
                />
              </label>
            </div>

            <div className="panel">
              <div className="issues-bar">
                <strong>Copy / empty plan</strong>
                <button type="button" onClick={() => setShowCreate((v) => !v)} disabled={termId === '' || busy}>
                  {showCreate ? 'Cancel' : 'New empty or copied plan'}
                </button>
              </div>
              {showCreate && (
                <form className="create-draft" onSubmit={onCreateDraft}>
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
                    Copy placements from
                    <select
                      value={copyFrom}
                      onChange={(e) => setCopyFrom(e.target.value ? Number(e.target.value) : '')}
                    >
                      <option value="">Empty</option>
                      {drafts.map((d) => (
                        <option key={d.draftId} value={d.draftId}>
                          {d.name}
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
            </div>

            <div className="panel">
              <div className="issues-bar">
                <strong>Roommate names</strong>
                <span className="muted">
                  {prepLoading
                    ? 'Loading…'
                    : prep
                      ? `${prep.summary.needsAttention} need a look`
                      : '—'}
                </span>
                <button type="button" onClick={() => setShowPrep((v) => !v)} disabled={termId === ''}>
                  {showPrep ? 'Hide' : 'Show'}
                </button>
                <button
                  type="button"
                  onClick={() => termId !== '' && reloadPrep(termId)}
                  disabled={termId === '' || busy || prepLoading || !showPrep}
                >
                  Refresh
                </button>
              </div>
              {showPrep && termId !== '' && (
                <>
                  <p className="prep-lede">
                    Fix names before generating when you can. Original wording stays unchanged.
                  </p>
                  <label className="prep-filter">
                    Filter
                    <select
                      value={prepFilter}
                      onChange={(e) => setPrepFilter(e.target.value as typeof prepFilter)}
                      disabled={prepLoading || !prep}
                    >
                      <option value="all">All open</option>
                      <option value="missing">Missing name</option>
                      <option value="duplicate">Duplicates</option>
                      <option value="nearOnly">Near spelling only</option>
                      <option value="exact">Exact ready</option>
                    </select>
                  </label>
                  {prepError && (
                    <p className="banner bad" role="alert">
                      {prepError}
                    </p>
                  )}
                  {!prepLoading && !prepError && prep && prepItems.length === 0 && (
                    <p className="empty">Nothing needs attention for this filter.</p>
                  )}
                  {prepItems.length > 0 && (
                    <ul className="prep-list">
                      {prepItems.map((item) => (
                        <li key={item.requestId} className={`prep-item kind-${item.conflictKind}`}>
                          <div className="prep-head">
                            <div>
                              <strong>{item.requesterName}</strong>{' '}
                              <span className="muted">{item.requesterFloorPlanCode}</span>
                              <br />
                              Asked for: <em>“{item.rawText}”</em>
                            </div>
                            <p className="prep-hint">{item.suggestedAction}</p>
                          </div>
                          {item.candidates.length > 0 ? (
                            <label className="prep-pick">
                              Candidate
                              <select
                                value={prepPicks[item.requestId] ?? ''}
                                onChange={(e) =>
                                  setPrepPicks((prev) => ({
                                    ...prev,
                                    [item.requestId]: e.target.value ? Number(e.target.value) : '',
                                  }))
                                }
                                disabled={busy}
                              >
                                <option value="">Select…</option>
                                {item.candidates.map((c) => (
                                  <option key={c.tenantId} value={c.tenantId}>
                                    {c.firstName} {c.lastName} · {c.matchKind}
                                  </option>
                                ))}
                              </select>
                            </label>
                          ) : (
                            <p className="muted">No candidates — do not invent a person.</p>
                          )}
                          <label className="prep-reason">
                            Reason (for mark actions)
                            <input
                              value={prepReasons[item.requestId] ?? ''}
                              onChange={(e) =>
                                setPrepReasons((prev) => ({
                                  ...prev,
                                  [item.requestId]: e.target.value,
                                }))
                              }
                              maxLength={500}
                              disabled={busy}
                            />
                          </label>
                          <div className="prep-actions">
                            <button
                              type="button"
                              disabled={busy || !operator.trim() || item.candidates.length === 0}
                              onClick={() => onResolvePrep(item)}
                            >
                              Confirm pick
                            </button>
                            <button
                              type="button"
                              disabled={busy || !operator.trim()}
                              onClick={() => onMarkPrep(item, 'unresolved')}
                            >
                              Unresolved
                            </button>
                            <button
                              type="button"
                              disabled={busy || !operator.trim()}
                              onClick={() => onMarkPrep(item, 'ambiguous')}
                            >
                              Ambiguous
                            </button>
                            <button
                              type="button"
                              disabled={busy || !operator.trim()}
                              onClick={() => onMarkPrep(item, 'excluded')}
                            >
                              Excluded
                            </button>
                          </div>
                        </li>
                      ))}
                    </ul>
                  )}
                </>
              )}
            </div>

            <div className="panel">
              <div className="issues-bar">
                <strong>Change history</strong>
                <span className="muted">
                  {historyLoading
                    ? 'Loading…'
                    : `${history.length} recent · ${history.filter((h) => h.isUndoable).length} undoable`}
                </span>
                <button type="button" onClick={() => setHistoryExpanded((v) => !v)}>
                  {historyExpanded ? 'Hide' : 'Show'}
                </button>
                <button
                  type="button"
                  disabled={draftId === '' || busy || historyLoading || !historyExpanded}
                  onClick={() => draftId !== '' && reloadHistory(draftId)}
                >
                  Refresh
                </button>
                <button
                  type="button"
                  disabled={
                    !editable || busy || !lastUndoable || !operator.trim() || !historyExpanded
                  }
                  onClick={() => onUndo(null)}
                >
                  Undo last
                </button>
              </div>
              {historyExpanded && historyError && (
                <p className="banner bad" role="alert">
                  {historyError}
                </p>
              )}
              {historyExpanded && !historyLoading && history.length === 0 && (
                <p className="empty">No recorded changes yet.</p>
              )}
              {historyExpanded && history.length > 0 && (
                <div className="history-table-wrap">
                  <table>
                    <thead>
                      <tr>
                        <th>ID</th>
                        <th>When</th>
                        <th>Type</th>
                        <th>By</th>
                        <th>Summary</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      {history.map((h) => (
                        <tr key={h.historyId} className={h.isUndoable ? undefined : 'history-undone'}>
                          <td>
                            <code>#{h.historyId}</code>
                          </td>
                          <td>{formatWhen(h.createdAt)}</td>
                          <td>
                            <code>{h.changeType}</code>
                          </td>
                          <td>{h.performedBy}</td>
                          <td>{summarizeChange(h, nameByCode)}</td>
                          <td>
                            {h.isUndoable ? (
                              <button
                                type="button"
                                disabled={!editable || busy || !operator.trim()}
                                onClick={() => onUndo(h.historyId)}
                              >
                                Undo
                              </button>
                            ) : (
                              '—'
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>

            {workspace && (
              <div className="panel">
                <div className="issues-bar">
                  <strong>Validation notes</strong>
                  <button type="button" onClick={() => setShowLimitations((v) => !v)}>
                    Limitations
                  </button>
                  <button type="button" onClick={() => setShowRequests((v) => !v)}>
                    All roommate wording ({workspace.roommateRequests.length})
                  </button>
                </div>
                {showLimitations && (
                  <ul className="limitations">
                    {workspace.validationLimitations.map((l) => (
                      <li key={l}>{l}</li>
                    ))}
                  </ul>
                )}
                {showRequests && (
                  <div className="requests">
                    <table>
                      <thead>
                        <tr>
                          <th>Requester</th>
                          <th>Wording</th>
                          <th>Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {workspace.roommateRequests.map((r) => (
                          <tr key={r.requestId}>
                            <td>{r.requesterName}</td>
                            <td>{r.rawText}</td>
                            <td>{r.status}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            )}
          </div>
        )}
      </section>

      {busy && (
        <p className="busy" role="status">
          Working…
        </p>
      )}
    </main>
  )
}
