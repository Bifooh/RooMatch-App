import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
} from 'react'
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
  Restriction,
  RoommatePrepItem,
  RoommatePrepPayload,
  RoommateRequest,
  TermInfo,
  UnassignedTenant,
  ValidationIssue,
  Workspace,
} from './types'

type BoardTab = 'women' | 'men'
type BoardLens = 'boxes' | 'list'
type AppScreen = 'board' | 'prep' | 'history'

type Selected =
  | { kind: 'assigned'; contractId: number; apartmentId: number }
  | { kind: 'unassigned'; contractId: number }
  | null

/** Concise Lodge plan reference (matches db/seed_small floor_plans). */
const FLOOR_PLAN_CODES: {
  code: string
  category: 'Women' | 'Men'
  shorthand: string
  name: string
  capacity: number
}[] = [
  { code: 'W-STD-2', category: 'Women', shorthand: 'Std 2bd', name: 'Standard 2 Bedroom, 2 Bath', capacity: 4 },
  { code: 'W-OVR-2', category: 'Women', shorthand: 'Ovr 2bd', name: 'Oversized 2 Bedroom, 2 Bath', capacity: 4 },
  { code: 'W-STD-3', category: 'Women', shorthand: 'Std 3bd', name: 'Standard 3 Bedroom, 3 Bath', capacity: 6 },
  { code: 'W-FLAT-4', category: 'Women', shorthand: 'Flat 4bd', name: 'Flat 4 Bedroom, 4 Bath', capacity: 8 },
  { code: 'W-STH-4', category: 'Women', shorthand: 'Std TH 4bd', name: 'Standard Townhome 4 Bedroom, 4 Bath', capacity: 8 },
  { code: 'W-OTH-4', category: 'Women', shorthand: 'Ovr TH 4bd', name: 'Oversized Townhome 4 Bedroom, 4 Bath', capacity: 8 },
  { code: 'M-STD-3', category: 'Men', shorthand: 'Std 3bd', name: 'Standard 3 Bedroom, 3 Bath', capacity: 6 },
  { code: 'M-FLAT-4', category: 'Men', shorthand: 'Flat 4bd', name: 'Flat 4 Bedroom, 4 Bath', capacity: 8 },
  { code: 'M-STH-4', category: 'Men', shorthand: 'Std TH 4bd', name: 'Standard Townhome 4 Bedroom, 4 Bath', capacity: 8 },
  { code: 'M-OTH-4', category: 'Men', shorthand: 'Ovr TH 4bd', name: 'Oversized Townhome 4 Bedroom, 4 Bath', capacity: 8 },
]

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

function buildingMatchesTab(building: string, apartmentNumber: string, tab: BoardTab) {
  const b = building.toLowerCase()
  if (tab === 'women') {
    if (b.includes('women')) return true
    if (b.includes('men')) return false
    return apartmentNumber.startsWith('3')
  }
  if (b.includes('women')) return false
  if (b.includes('men')) return true
  return apartmentNumber.startsWith('2')
}

function planMatchesTab(floorPlanCode: string, tab: BoardTab) {
  const code = floorPlanCode.toUpperCase()
  if (tab === 'women') return code.startsWith('W-') || code.startsWith('W_')
  return code.startsWith('M-') || code.startsWith('M_')
}

function labelTenantType(tenantType: string) {
  if (tenantType === 'renewing') return 'Renewing'
  if (tenantType === 'new') return 'New'
  return tenantType
}

function labelStayPreference(stay: string | null | undefined) {
  if (!stay) return null
  if (stay === 'stay') return 'Prefer stay'
  if (stay === 'move') return 'Open to move'
  if (stay === 'no_preference') return 'No preference'
  return stay
}

/** Short colored tag matching chip colors (for Adjust + details peek). */
function statusTagMeta(tenantType: string, stayPreference: string | null | undefined) {
  if (tenantType === 'renewing') {
    if (stayPreference === 'stay') {
      return { className: 'status-tag chip-renew-stay', title: 'Renewing' }
    }
    if (stayPreference === 'move') {
      return { className: 'status-tag chip-renew-move', title: 'Open to move' }
    }
    return { className: 'status-tag chip-renew', title: 'Renewing' }
  }
  return { className: 'status-tag chip-new', title: 'New' }
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
  const [renewingOnly, setRenewingOnly] = useState(false)
  const [lockedOnly, setLockedOnly] = useState(false)
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
  const [appScreen, setAppScreen] = useState<AppScreen>('board')
  const [showColorGuide, setShowColorGuide] = useState(false)
  const [showFloorPlanCodes, setShowFloorPlanCodes] = useState(false)
  const [boardTab, setBoardTab] = useState<BoardTab>('women')
  const [boardLens, setBoardLens] = useState<BoardLens>('boxes')
  const [detailsContractId, setDetailsContractId] = useState<number | null>(null)
  const [dragContractId, setDragContractId] = useState<number | null>(null)
  const [dropHover, setDropHover] = useState<number | 'waiting' | null>(null)
  const dragMovedRef = useRef(false)
  const dragContractIdRef = useRef<number | null>(null)
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
        // Prefer newest plan so managers land on recent work, not seed drafts.
        if (d.length) setDraftId(d[d.length - 1].draftId)
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
    return [
      ...new Set(
        workspace.apartments
          .filter((a) => buildingMatchesTab(a.building, a.apartmentNumber, boardTab))
          .map((a) => a.floorPlanCode),
      ),
    ].sort()
  }, [workspace, boardTab])

  function personMatchesManagerFilters(tenantType: string, isLocked: boolean) {
    if (renewingOnly && tenantType !== 'renewing') return false
    if (lockedOnly && !isLocked) return false
    return true
  }

  const filtersActive =
    Boolean(query.trim()) || Boolean(planFilter) || renewingOnly || lockedOnly

  function clearBoardFilters() {
    setQuery('')
    setPlanFilter('')
    setRenewingOnly(false)
    setLockedOnly(false)
  }

  function rosterForApartment(a: {
    apartmentNumber: string
    floorPlanCode: string
    occupants: {
      contractId: number
      firstName: string
      lastName: string
      tenantCode: string
      tenantType: string
      stayPreference: string | null
      isLocked: boolean
    }[]
  }) {
    const people = a.occupants.filter((o) =>
      personMatchesManagerFilters(o.tenantType, o.isLocked),
    )
    if (renewingOnly || lockedOnly) {
      return people.filter((o) =>
        matchesQuery(query, {
          name: `${o.firstName} ${o.lastName}`,
          code: o.tenantCode,
          apartment: a.apartmentNumber,
          plan: a.floorPlanCode,
        }),
      )
    }
    const aptHit =
      !query.trim() ||
      matchesQuery(query, {
        apartment: a.apartmentNumber,
        plan: a.floorPlanCode,
      })
    if (aptHit) return a.occupants
    return a.occupants.filter((o) =>
      matchesQuery(query, {
        name: `${o.firstName} ${o.lastName}`,
        code: o.tenantCode,
        apartment: a.apartmentNumber,
        plan: a.floorPlanCode,
      }),
    )
  }

  const filteredApartments = useMemo(() => {
    if (!workspace) return []
    const personFilters = renewingOnly || lockedOnly
    return workspace.apartments.filter((a) => {
      if (!buildingMatchesTab(a.building, a.apartmentNumber, boardTab)) return false
      if (planFilter && a.floorPlanCode !== planFilter) return false

      const matchingPeople = a.occupants.filter((o) =>
        personMatchesManagerFilters(o.tenantType, o.isLocked),
      )
      const queryPeople = matchingPeople.filter((o) =>
        matchesQuery(query, {
          name: `${o.firstName} ${o.lastName}`,
          code: o.tenantCode,
          apartment: a.apartmentNumber,
          plan: a.floorPlanCode,
        }),
      )

      if (personFilters) {
        return query.trim() ? queryPeople.length > 0 : matchingPeople.length > 0
      }

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
  }, [workspace, query, planFilter, boardTab, renewingOnly, lockedOnly])

  const filteredUnassigned = useMemo(() => {
    if (!workspace) return []
    return workspace.unassigned.filter((u) => {
      if (!planMatchesTab(u.floorPlanCode, boardTab)) return false
      if (planFilter && u.floorPlanCode !== planFilter) return false
      if (!personMatchesManagerFilters(u.tenantType, false)) return false
      return matchesQuery(query, {
        name: `${u.firstName} ${u.lastName}`,
        code: u.tenantCode,
        plan: u.floorPlanCode,
      })
    })
  }, [workspace, query, planFilter, boardTab, renewingOnly, lockedOnly])

  /** One student per row for the List sheet lens (same filters as Boxes). */
  const sheetRows = useMemo(() => {
    const rows: Array<{
      key: string
      kind: 'assigned' | 'waiting'
      apartmentLabel: string
      floorPlanCode: string
      fill: string
      contractId: number
      apartmentId: number | null
      firstName: string
      lastName: string
      tenantType: string
      stayPreference: string | null
      isLocked: boolean
    }> = []

    for (const a of filteredApartments) {
      const fill = `${a.occupants.length}/${a.effectiveCapacity}`
      for (const o of rosterForApartment(a)) {
        rows.push({
          key: `a-${o.contractId}`,
          kind: 'assigned',
          apartmentLabel: a.apartmentNumber,
          floorPlanCode: a.floorPlanCode,
          fill,
          contractId: o.contractId,
          apartmentId: a.apartmentId,
          firstName: o.firstName,
          lastName: o.lastName,
          tenantType: o.tenantType,
          stayPreference: o.stayPreference,
          isLocked: o.isLocked,
        })
      }
    }

    for (const u of filteredUnassigned) {
      rows.push({
        key: `w-${u.contractId}`,
        kind: 'waiting',
        apartmentLabel: 'Waiting',
        floorPlanCode: u.floorPlanCode,
        fill: '—',
        contractId: u.contractId,
        apartmentId: null,
        firstName: u.firstName,
        lastName: u.lastName,
        tenantType: u.tenantType,
        stayPreference: u.stayPreference,
        isLocked: false,
      })
    }

    return rows
  }, [filteredApartments, filteredUnassigned, query, renewingOnly, lockedOnly])

  const waitingOtherTab = useMemo(() => {
    if (!workspace) return 0
    const other: BoardTab = boardTab === 'women' ? 'men' : 'women'
    return workspace.unassigned.filter((u) => planMatchesTab(u.floorPlanCode, other)).length
  }, [workspace, boardTab])

  const studentDetails = useMemo(() => {
    if (!workspace || detailsContractId == null) return null
    const contractId = detailsContractId
    let firstName = ''
    let lastName = ''
    let tenantCode = ''
    let tenantType = ''
    let stayPreference: string | null = null
    let floorPlanCode = ''
    let apartmentNumber: string | null = null
    let isLocked = false
    let lockedBy: string | null = null
    let assignmentSource: string | null = null
    let placed = false

    const un = workspace.unassigned.find((u) => u.contractId === contractId)
    if (un) {
      firstName = un.firstName
      lastName = un.lastName
      tenantCode = un.tenantCode
      tenantType = un.tenantType
      stayPreference = un.stayPreference
      floorPlanCode = un.floorPlanCode
    } else {
      for (const a of workspace.apartments) {
        const o = a.occupants.find((x) => x.contractId === contractId)
        if (!o) continue
        firstName = o.firstName
        lastName = o.lastName
        tenantCode = o.tenantCode
        tenantType = o.tenantType
        stayPreference = o.stayPreference
        floorPlanCode = a.floorPlanCode
        apartmentNumber = a.apartmentNumber
        isLocked = o.isLocked
        lockedBy = o.lockedBy
        assignmentSource = o.assignmentSource
        placed = true
        break
      }
    }
    if (!tenantCode) return null

    const requests: RoommateRequest[] = workspace.roommateRequests.filter(
      (r) => r.contractId === contractId,
    )
    const restrictions: Restriction[] = workspace.restrictions.filter(
      (r) => r.contractId === contractId,
    )

    return {
      contractId,
      firstName,
      lastName,
      tenantCode,
      tenantType,
      stayPreference,
      floorPlanCode,
      apartmentNumber,
      placed,
      isLocked,
      lockedBy,
      assignmentSource,
      requests,
      restrictions,
    }
  }, [workspace, detailsContractId])

  const detailsStatusTag = studentDetails
    ? statusTagMeta(studentDetails.tenantType, studentDetails.stayPreference)
    : null

  const selectedInfo = useMemo(() => {
    if (!workspace || !selected) return null
    if (selected.kind === 'unassigned') {
      const u = workspace.unassigned.find((x) => x.contractId === selected.contractId)
      if (!u) return null
      const name = `${u.firstName} ${u.lastName}`
      return {
        name,
        meta: u.tenantCode,
        title: displayName(u.firstName, u.lastName, u.tenantCode),
      }
    }
    for (const a of workspace.apartments) {
      const o = a.occupants.find((x) => x.contractId === selected.contractId)
      if (!o) continue
      const name = `${o.firstName} ${o.lastName}`
      return {
        name,
        meta: `${o.tenantCode} · apt ${a.apartmentNumber}`,
        title: `${displayName(o.firstName, o.lastName, o.tenantCode)} · apt ${a.apartmentNumber}`,
      }
    }
    return null
  }, [workspace, selected])

  const selectedStatus = useMemo(() => {
    if (!workspace || !selected) return null
    if (selected.kind === 'unassigned') {
      const u = workspace.unassigned.find((x) => x.contractId === selected.contractId)
      return u ? statusTagMeta(u.tenantType, u.stayPreference) : null
    }
    for (const a of workspace.apartments) {
      const o = a.occupants.find((x) => x.contractId === selected.contractId)
      if (o) return statusTagMeta(o.tenantType, o.stayPreference)
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
            label: `${o.firstName} ${o.lastName} · ${a.apartmentNumber}`,
          })),
      )
      .sort((a, b) => a.label.localeCompare(b.label))
  }, [workspace, selected])

  function contractPlan(contractId: number): { floorPlanId: number; floorPlanCode: string } | null {
    if (!workspace) return null
    const un = workspace.unassigned.find((u) => u.contractId === contractId)
    if (un) return { floorPlanId: un.floorPlanId, floorPlanCode: un.floorPlanCode }
    for (const a of workspace.apartments) {
      const o = a.occupants.find((x) => x.contractId === contractId)
      if (o) return { floorPlanId: o.floorPlanId, floorPlanCode: o.floorPlanCode }
    }
    return null
  }

  const compatibleApartments = useMemo(() => {
    if (!workspace || !selected) return workspace?.apartments ?? []
    const plan = contractPlan(selected.contractId)
    if (!plan) return []
    return workspace.apartments
      .filter((a) => a.floorPlanId === plan.floorPlanId)
      .sort((a, b) => a.apartmentNumber.localeCompare(b.apartmentNumber))
  }, [workspace, selected])

  const dragPlan = useMemo(
    () => (dragContractId != null ? contractPlan(dragContractId) : null),
    [dragContractId, workspace],
  )

  function apartmentIsDropValid(apartmentId: number, contractId: number) {
    if (!workspace) return false
    const plan = contractPlan(contractId)
    const apt = workspace.apartments.find((a) => a.apartmentId === apartmentId)
    if (!plan || !apt) return false
    if (apt.floorPlanId !== plan.floorPlanId) return false
    const already = apt.occupants.some((o) => o.contractId === contractId)
    if (already) return false
    return apt.occupants.length < apt.effectiveCapacity
  }

  function operatorName() {
    return operator.trim() || 'Fictional Operator'
  }

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
    if (termId === '' || busy || matching) return
    setMatching(true)
    setBusy(true)
    setBanner(null)
    setMatchSummary(null)
    try {
      const res = await runAutoMatch(termId, {
        draftName: null,
        copyFromDraftId: draftId === '' ? null : draftId,
        copyLocks: true,
        performedBy: operator.trim() || 'Fictional Operator',
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

  function selectAssigned(contractId: number, apartmentId: number) {
    setSelected({ kind: 'assigned', contractId, apartmentId })
    setSwapTarget('')
    setMoveTarget('')
  }

  function openDetails(contractId: number) {
    setDetailsContractId(contractId)
  }

  function beginDrag(contractId: number, e: DragEvent) {
    dragMovedRef.current = false
    dragContractIdRef.current = contractId
    setDragContractId(contractId)
    e.dataTransfer.setData('text/plain', String(contractId))
    e.dataTransfer.effectAllowed = 'move'
  }

  function endDrag() {
    dragContractIdRef.current = null
    setDragContractId(null)
    setDropHover(null)
  }

  function onNameClick(select: () => void) {
    if (dragMovedRef.current) {
      dragMovedRef.current = false
      return
    }
    select()
  }

  function contractIdFromDrop(e: DragEvent) {
    const raw = e.dataTransfer.getData('text/plain')
    const parsed = raw ? Number(raw) : NaN
    if (Number.isFinite(parsed) && parsed > 0) return parsed
    return dragContractIdRef.current
  }

  function dropOnApartment(apartmentId: number, e: DragEvent) {
    const contractId = contractIdFromDrop(e)
    endDrag()
    if (draftId === '' || !editable || busy || contractId == null) return
    const plan = contractPlan(contractId)
    const apt = workspace?.apartments.find((a) => a.apartmentId === apartmentId)
    if (!plan || !apt) return
    if (apt.occupants.some((o) => o.contractId === contractId)) return
    if (apt.floorPlanId !== plan.floorPlanId) {
      setBanner({
        kind: 'bad',
        text: `Wrong floor plan: need ${plan.floorPlanCode}, apartment ${apt.apartmentNumber} is ${apt.floorPlanCode}.`,
      })
      return
    }
    if (apt.occupants.length >= apt.effectiveCapacity) {
      setBanner({
        kind: 'bad',
        text: `Apartment ${apt.apartmentNumber} is full (${apt.occupants.length}/${apt.effectiveCapacity}).`,
      })
      return
    }
    // Same-plan with free space: call API (may still reject locks/restrictions).
    void runMutation(() =>
      moveTenant(draftId, contractId, apartmentId, operatorName(), changeReason.trim() || null),
    )
  }

  function dropUnassign(e: DragEvent) {
    const contractId = contractIdFromDrop(e)
    endDrag()
    if (draftId === '' || !editable || busy || contractId == null) return
    const isWaiting = workspace?.unassigned.some((u) => u.contractId === contractId)
    if (isWaiting) return
    void runMutation(() =>
      moveTenant(draftId, contractId, null, operatorName(), changeReason.trim() || null),
    )
  }

  function closeDetails() {
    setDetailsContractId(null)
  }

  function closeFloorPlanCodes() {
    setShowFloorPlanCodes(false)
  }

  function openScreen(screen: AppScreen) {
    setAppScreen(screen)
    setDetailsContractId(null)
    setShowFloorPlanCodes(false)
    setShowColorGuide(false)
    if (screen === 'prep' && termId !== '') void reloadPrep(termId)
    if (screen === 'history' && draftId !== '') void reloadHistory(draftId)
  }

  function backToBoard() {
    setAppScreen('board')
  }

  useEffect(() => {
    if (detailsContractId == null && !showFloorPlanCodes) return
    function onKey(e: KeyboardEvent) {
      if (e.key !== 'Escape') return
      e.preventDefault()
      if (showFloorPlanCodes) {
        setShowFloorPlanCodes(false)
        return
      }
      setDetailsContractId(null)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [detailsContractId, showFloorPlanCodes])

  useEffect(() => {
    if (planFilter && !plans.includes(planFilter)) setPlanFilter('')
  }, [boardTab, plans, planFilter])

  /** Chip color from contracts.tenant_type + stay_preference (renewals only). */
  function tenantChipClass(tenantType: string, stayPreference: string | null | undefined) {
    if (tenantType === 'renewing') {
      if (stayPreference === 'stay') return 'chip-renew-stay'
      if (stayPreference === 'move') return 'chip-renew-move'
      return 'chip-renew'
    }
    return 'chip-new'
  }

  function chipClassName(
    selectedHere: boolean,
    tenantType: string,
    stayPreference: string | null | undefined,
  ) {
    return [
      'chip',
      tenantChipClass(tenantType, stayPreference),
      selectedHere ? 'selected' : '',
    ]
      .filter(Boolean)
      .join(' ')
  }

  return (
    <main className="app">
      <header className="hero hero-compact" aria-label="Roomatch plan setup">
        <h1 className="hero-brand">Roomatch</h1>
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
            disabled={termId === '' || busy || matching}
            aria-busy={matching}
          >
            {matching
              ? 'Making plan…'
              : drafts.length === 0
                ? 'Make first plan'
                : 'Make another plan'}
          </button>
        </div>
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
          Last plan: {matchSummary}
        </p>
      )}

      {appScreen === 'board' && termId !== '' && (
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
          <label className="plan-strip-operator">
            Your name
            <input
              value={operator}
              onChange={(e) => setOperator(e.target.value)}
              placeholder="For locks & undo"
            />
          </label>
          {workspace && (
            <p className="plan-meta">
              {workspace.apartments.reduce((n, a) => n + a.occupants.length, 0)} placed ·{' '}
              {workspace.unassigned.length} unassigned
              {!editable ? ' · read-only' : ''}
            </p>
          )}
        </section>
      )}

      {appScreen === 'board' && workspace && (
        <>
          <div className="board-head">
            <div className="board-title-row">
              <h2>Apartments</h2>
              <div className="board-tabs" role="tablist" aria-label="Building">
                <button
                  type="button"
                  role="tab"
                  aria-selected={boardTab === 'women'}
                  className={boardTab === 'women' ? 'tab active' : 'tab'}
                  onClick={() => setBoardTab('women')}
                >
                  Women
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={boardTab === 'men'}
                  className={boardTab === 'men' ? 'tab active' : 'tab'}
                  onClick={() => setBoardTab('men')}
                >
                  Men
                </button>
              </div>
              <div className="board-tabs lens-tabs" role="tablist" aria-label="Board view">
                <button
                  type="button"
                  role="tab"
                  aria-selected={boardLens === 'boxes'}
                  className={boardLens === 'boxes' ? 'tab active' : 'tab'}
                  onClick={() => setBoardLens('boxes')}
                >
                  Boxes
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={boardLens === 'list'}
                  className={boardLens === 'list' ? 'tab active' : 'tab'}
                  onClick={() => setBoardLens('list')}
                >
                  List
                </button>
              </div>
            </div>
            <div className="board-filters" aria-label="Board filters">
              <label className="filter-find">
                Find
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Name or apartment"
                />
              </label>
              <div className="filter-chip-bar" role="group" aria-label="Plan filters">
                <button
                  type="button"
                  className={`filter-chip ${renewingOnly ? 'is-pressed' : ''}`}
                  aria-pressed={renewingOnly}
                  onClick={() => setRenewingOnly((v) => !v)}
                >
                  <span className="filter-chip-mark" aria-hidden="true">
                    {renewingOnly ? '✓' : ''}
                  </span>
                  Renewing only
                </button>
                <button
                  type="button"
                  className={`filter-chip ${lockedOnly ? 'is-pressed' : ''}`}
                  aria-pressed={lockedOnly}
                  onClick={() => setLockedOnly((v) => !v)}
                >
                  <span className="filter-chip-mark" aria-hidden="true">
                    {lockedOnly ? '✓' : ''}
                  </span>
                  Locked only
                </button>
                <label
                  className={`filter-chip filter-chip-select ${planFilter ? 'is-pressed' : ''}`}
                >
                  <span className="filter-chip-mark" aria-hidden="true">
                    {planFilter ? '✓' : ''}
                  </span>
                  <span className="filter-chip-select-label">Floor plan</span>
                  <select
                    value={planFilter}
                    onChange={(e) => setPlanFilter(e.target.value)}
                    aria-label="Floor plan filter"
                  >
                    <option value="">All on this tab</option>
                    {plans.map((p) => (
                      <option key={p} value={p}>
                        {p}
                      </option>
                    ))}
                  </select>
                </label>
                {filtersActive && (
                  <>
                    <span className="filter-showing" aria-live="polite">
                      Showing {sheetRows.length}
                    </span>
                    <button type="button" className="filter-clear" onClick={clearBoardFilters}>
                      Clear
                    </button>
                  </>
                )}
              </div>
              <div className="board-quiet" role="group" aria-label="Board links">
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => openScreen('prep')}
                  disabled={termId === ''}
                >
                  Prep
                  {prep && prep.summary.needsAttention > 0
                    ? ` (${prep.summary.needsAttention})`
                    : ''}
                </button>
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => openScreen('history')}
                  disabled={draftId === ''}
                >
                  History
                </button>
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => setShowCreate((v) => !v)}
                  disabled={termId === '' || busy}
                  aria-expanded={showCreate}
                >
                  {showCreate ? 'Hide copy' : 'Copy plan'}
                </button>
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => {
                    setShowLimitations((v) => !v)
                    setShowRequests(false)
                  }}
                  aria-expanded={showLimitations}
                >
                  {showLimitations ? 'Hide notes' : 'Notes'}
                </button>
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => setShowColorGuide((v) => !v)}
                  aria-expanded={showColorGuide}
                >
                  {showColorGuide ? 'Hide colors' : 'Colors'}
                </button>
                <button
                  type="button"
                  className="quiet-control"
                  onClick={() => setShowFloorPlanCodes(true)}
                  aria-haspopup="dialog"
                >
                  Floor plans
                </button>
              </div>
            </div>
          </div>

          {showCreate && (
            <div className="panel plan-admin-panel" aria-label="Copy or empty plan">
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
            </div>
          )}

          {showLimitations && (
            <div className="panel plan-admin-panel" aria-label="Validation notes">
              <div className="issues-bar">
                <strong>Validation notes</strong>
                <button
                  type="button"
                  onClick={() => setShowRequests((v) => !v)}
                >
                  All roommate wording ({workspace.roommateRequests.length})
                </button>
              </div>
              <ul className="limitations">
                {workspace.validationLimitations.map((l) => (
                  <li key={l}>{l}</li>
                ))}
              </ul>
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

          {showColorGuide && (
            <div className="color-guide" role="note">
              <p>
                <strong>Student chips</strong> use contract status: Renewing (warm), Open to move
                (amber), New (blue).
              </p>
              <ul className="color-guide-swatches" aria-label="Student chip colors">
                <li>
                  <span className="swatch chip-renew-stay" /> Renewing
                </li>
                <li>
                  <span className="swatch chip-renew-move" /> Open to move
                </li>
                <li>
                  <span className="swatch chip-renew" /> Renewing
                </li>
                <li>
                  <span className="swatch chip-new" /> New
                </li>
              </ul>
              <p>
                <strong>Apartment boxes</strong> keep a light building tint so Women’s (cool) and
                Men’s (green) stay scannable — separate from chip colors.
              </p>
              <ul className="color-guide-swatches" aria-label="Apartment box colors">
                <li>
                  <span className="swatch box-women" /> Women’s building
                </li>
                <li>
                  <span className="swatch box-men" /> Men’s building
                </li>
              </ul>
            </div>
          )}

          <div className="workspace-layout">
            <aside className="side-col side-left" aria-label="Adjust column">
              <section className="panel adjust-inspector" aria-label="Adjust selection">
                <h2>Adjust</h2>
                {!selected && (
                  <p className="muted move-hint">Drag a student onto a highlighted apartment.</p>
                )}
                {selected && selectedInfo && (
                  <>
                    <div className="selected-line">
                      <div className="selected-name-block" title={selectedInfo.title}>
                        <strong className="selected-name">{selectedInfo.name}</strong>
                        <span className="selected-meta-row">
                          {selectedStatus && (
                            <span className={selectedStatus.className}>{selectedStatus.title}</span>
                          )}
                          <span className="muted selected-meta">{selectedInfo.meta}</span>
                        </span>
                      </div>
                      <button
                        type="button"
                        className="details-btn"
                        onClick={() => openDetails(selected.contractId)}
                      >
                        Details
                      </button>
                    </div>
                    <p className="muted move-hint">
                      Drag to move
                      {contractPlan(selected.contractId)
                        ? ` · ${contractPlan(selected.contractId)!.floorPlanCode}`
                        : ''}
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
                          <option value="">Same-plan apartment…</option>
                          {compatibleApartments.length === 0 && (
                            <option value="" disabled>
                              No apartments for this floor plan
                            </option>
                          )}
                          {compatibleApartments.map((a) => {
                            const free = a.effectiveCapacity - a.occupants.length
                            const here =
                              selected.kind === 'assigned' &&
                              selected.apartmentId === a.apartmentId
                            return (
                              <option
                                key={a.apartmentId}
                                value={a.apartmentId}
                                disabled={here || free <= 0}
                              >
                                {a.apartmentNumber} · {a.floorPlanCode} · fl {a.floor} ·{' '}
                                {a.occupants.length}/{a.effectiveCapacity}
                                {free <= 0 ? ' (full)' : ''}
                                {here ? ' (here)' : ''}
                              </option>
                            )
                          })}
                        </select>
                      </label>
                      <div className="action-row-actions">
                        <button
                          type="button"
                          disabled={!editable || busy || moveTarget === ''}
                          onClick={() =>
                            runMutation(() =>
                              moveTenant(
                                draftId as number,
                                selected.contractId,
                                moveTarget as number,
                                operatorName(),
                                changeReason.trim() || null,
                              ),
                            )
                          }
                        >
                          Move
                        </button>
                        <button
                          type="button"
                          disabled={!editable || busy || selected.kind !== 'assigned'}
                          onClick={() =>
                            runMutation(() =>
                              moveTenant(
                                draftId as number,
                                selected.contractId,
                                null,
                                operatorName(),
                                changeReason.trim() || null,
                              ),
                            )
                          }
                        >
                          Unassign
                        </button>
                      </div>
                    </div>
                    {selected.kind === 'assigned' && (
                      <details className="adjust-more">
                        <summary>More (swap, lock)</summary>
                        <p className="muted move-hint">
                          Same floor plan only. Highlighted boxes accept a drop.
                        </p>
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
                          <div className="action-row-actions">
                            <button
                              type="button"
                              disabled={!editable || busy || swapTarget === ''}
                              onClick={() =>
                                runMutation(() =>
                                  swapTenants(
                                    draftId as number,
                                    selected.contractId,
                                    swapTarget as number,
                                    operatorName(),
                                    changeReason.trim() || null,
                                  ),
                                )
                              }
                            >
                              Swap
                            </button>
                            <button
                              type="button"
                              disabled={!editable || busy}
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
                                    operatorName(),
                                    changeReason.trim() || null,
                                  ),
                                )
                              }}
                            >
                              Toggle lock
                            </button>
                          </div>
                        </div>
                      </details>
                    )}
                  </>
                )}
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

            <section
              className="board-col"
              aria-label={boardLens === 'list' ? 'Plan sheet' : 'Apartment boxes'}
            >
              {boardLens === 'boxes' && filteredApartments.length === 0 && (
                <p className="empty">No apartments match.</p>
              )}
              {boardLens === 'boxes' ? (
                <ul className="apt-board">
                  {filteredApartments.map((a) => {
                    const fill = aptFillClass(a.occupants.length, a.effectiveCapacity)
                    const bldg = buildingClass(a.building)
                    const roster = rosterForApartment(a)
                    const dragging = dragContractId != null
                    const valid =
                      dragging && dragContractId != null
                        ? apartmentIsDropValid(a.apartmentId, dragContractId)
                        : false
                    const samePlan =
                      dragging && dragPlan != null && a.floorPlanId === dragPlan.floorPlanId
                    const dropClass = dragging
                      ? valid
                        ? 'drop-valid'
                        : samePlan
                          ? 'drop-full'
                          : 'drop-invalid'
                      : ''
                    return (
                      <li
                        key={a.apartmentId}
                        className={`apt-box ${bldg} ${fill} ${dropClass} ${dropHover === a.apartmentId ? 'drop-hover' : ''}`}
                        onDragOver={(e) => {
                          const draggingId = dragContractIdRef.current ?? dragContractId
                          if (draggingId == null && !e.dataTransfer.types.includes('text/plain'))
                            return
                          e.preventDefault()
                          const id = draggingId
                          const ok = id != null && apartmentIsDropValid(a.apartmentId, id)
                          e.dataTransfer.dropEffect = ok ? 'move' : 'none'
                          setDropHover(a.apartmentId)
                        }}
                        onDragLeave={() => {
                          setDropHover((h) => (h === a.apartmentId ? null : h))
                        }}
                        onDrop={(e) => {
                          e.preventDefault()
                          dropOnApartment(a.apartmentId, e)
                        }}
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
                          {a.occupants.length === 0 && <li className="apt-empty">Empty</li>}
                          {a.occupants.length > 0 && roster.length === 0 && (
                            <li className="apt-empty">No matches in this apartment</li>
                          )}
                          {roster.map((o) => (
                            <li key={o.contractId} className="chip-row">
                              <button
                                type="button"
                                draggable={editable && !busy}
                                className={chipClassName(
                                  selected?.contractId === o.contractId,
                                  o.tenantType,
                                  o.stayPreference,
                                )}
                                onDragStart={(e) => {
                                  dragMovedRef.current = true
                                  selectAssigned(o.contractId, a.apartmentId)
                                  beginDrag(o.contractId, e)
                                }}
                                onDragEnd={endDrag}
                                onClick={() =>
                                  onNameClick(() => selectAssigned(o.contractId, a.apartmentId))
                                }
                              >
                                {o.firstName} {o.lastName}
                                {o.isLocked ? <span className="lock-mark"> · locked</span> : null}
                              </button>
                              <button
                                type="button"
                                className="chip-info"
                                aria-label={`Details for ${o.firstName} ${o.lastName}`}
                                title="Details"
                                onClick={() => openDetails(o.contractId)}
                              >
                                i
                              </button>
                            </li>
                          ))}
                        </ul>
                      </li>
                    )
                  })}
                </ul>
              ) : sheetRows.length === 0 ? (
                <p className="empty">No students match on this tab (or filters hide them).</p>
              ) : (
                <div className="plan-sheet-wrap">
                  <table className="plan-sheet">
                    <thead>
                      <tr>
                        <th scope="col">Apartment</th>
                        <th scope="col">Floor plan</th>
                        <th scope="col">Fill</th>
                        <th scope="col">Name</th>
                        <th scope="col">Status</th>
                        <th scope="col">Locked</th>
                        <th scope="col">
                          <span className="visually-hidden">Details</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {sheetRows.map((row) => {
                        const tag = statusTagMeta(row.tenantType, row.stayPreference)
                        const isSelected = selected?.contractId === row.contractId
                        return (
                          <tr
                            key={row.key}
                            className={`${row.kind === 'waiting' ? 'is-waiting' : ''} ${isSelected ? 'is-selected' : ''}`}
                          >
                            <td className="sheet-apt">{row.apartmentLabel}</td>
                            <td className="sheet-plan">{row.floorPlanCode}</td>
                            <td className="sheet-fill">{row.fill}</td>
                            <td className="sheet-name">
                              <button
                                type="button"
                                className="plan-list-name"
                                onClick={() => {
                                  if (row.kind === 'assigned' && row.apartmentId != null) {
                                    selectAssigned(row.contractId, row.apartmentId)
                                  } else {
                                    const u = filteredUnassigned.find(
                                      (x) => x.contractId === row.contractId,
                                    )
                                    if (u) selectUnassigned(u)
                                  }
                                }}
                              >
                                {row.firstName} {row.lastName}
                              </button>
                            </td>
                            <td className="sheet-status">
                              <span className={tag.className}>{tag.title}</span>
                            </td>
                            <td className="sheet-lock">{row.isLocked ? 'Yes' : '—'}</td>
                            <td className="sheet-info">
                              <button
                                type="button"
                                className="chip-info"
                                aria-label={`Details for ${row.firstName} ${row.lastName}`}
                                title="Details"
                                onClick={() => openDetails(row.contractId)}
                              >
                                i
                              </button>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </section>


            <aside
              className="side-col side-right"
              aria-label={studentDetails ? 'Student details' : 'Waiting column'}
            >
              {studentDetails ? (
                <section
                  className="panel student-peek"
                  role="dialog"
                  aria-modal="false"
                  aria-labelledby="student-details-title"
                >
                  <div className="peek-head">
                    <div className="modal-title-block">
                      <h2 id="student-details-title">
                        {studentDetails.firstName} {studentDetails.lastName}
                      </h2>
                      {detailsStatusTag && (
                        <span className={detailsStatusTag.className}>{detailsStatusTag.title}</span>
                      )}
                    </div>
                    <button
                      type="button"
                      className="peek-close"
                      onClick={closeDetails}
                      aria-label="Close"
                      title="Close"
                      autoFocus
                    >
                      <span aria-hidden="true">×</span>
                    </button>
                  </div>
                  <dl className="detail-grid">
                    <div>
                      <dt>Tenant code</dt>
                      <dd>{studentDetails.tenantCode}</dd>
                    </div>
                    <div>
                      <dt>Contract type</dt>
                      <dd>{labelTenantType(studentDetails.tenantType)}</dd>
                    </div>
                    <div>
                      <dt>Stay preference</dt>
                      <dd>{labelStayPreference(studentDetails.stayPreference) ?? '—'}</dd>
                    </div>
                    <div>
                      <dt>Floor plan</dt>
                      <dd>{studentDetails.floorPlanCode || '—'}</dd>
                    </div>
                    <div>
                      <dt>Current apartment</dt>
                      <dd>
                        {studentDetails.placed
                          ? studentDetails.apartmentNumber ?? '—'
                          : 'Unassigned (waiting)'}
                      </dd>
                    </div>
                    {studentDetails.placed && (
                      <>
                        <div>
                          <dt>Lock</dt>
                          <dd>
                            {studentDetails.isLocked
                              ? `Locked${studentDetails.lockedBy ? ` by ${studentDetails.lockedBy}` : ''}`
                              : 'Not locked'}
                          </dd>
                        </div>
                        {studentDetails.assignmentSource && (
                          <div>
                            <dt>Assignment source</dt>
                            <dd>{studentDetails.assignmentSource}</dd>
                          </div>
                        )}
                      </>
                    )}
                  </dl>

                  <section className="modal-section" aria-label="Roommate requests">
                    <h3>Roommate request wording</h3>
                    {studentDetails.requests.length === 0 ? (
                      <p className="muted">None on file for this contract.</p>
                    ) : (
                      <ul className="modal-list">
                        {studentDetails.requests.map((r) => (
                          <li key={r.requestId}>
                            <span className="mono">“{r.rawText}”</span>
                            <span className="muted">
                              {' '}
                              · {r.status}
                              {r.exclusionReason ? ` · ${r.exclusionReason}` : ''}
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </section>

                  <section className="modal-section" aria-label="Restrictions">
                    <h3>Restrictions</h3>
                    {studentDetails.restrictions.length === 0 ? (
                      <p className="muted">None on file for this contract.</p>
                    ) : (
                      <ul className="modal-list">
                        {studentDetails.restrictions.map((r) => (
                          <li key={r.restrictionId}>
                            <strong>{r.restrictionType}</strong>
                            {r.value ? ` = ${r.value}` : ''}
                            {r.description ? ` — ${r.description}` : ''}
                          </li>
                        ))}
                      </ul>
                    )}
                  </section>

                  <p className="muted modal-hint">
                    Escape returns Waiting. Board stays open — use Adjust or drag to move.
                  </p>
                </section>
              ) : (
                <section
                  className={`panel waiting-drop ${dragContractId != null ? 'drop-unassign' : ''} ${dropHover === 'waiting' ? 'drop-hover' : ''}`}
                  aria-label="Waiting list"
                  onDragOver={(e) => {
                    const draggingId = dragContractIdRef.current ?? dragContractId
                    if (draggingId == null && !e.dataTransfer.types.includes('text/plain')) return
                    const id = draggingId
                    if (id != null && workspace.unassigned.some((u) => u.contractId === id)) return
                    e.preventDefault()
                    e.dataTransfer.dropEffect = 'move'
                    setDropHover('waiting')
                  }}
                  onDragLeave={() => setDropHover((h) => (h === 'waiting' ? null : h))}
                  onDrop={(e) => {
                    e.preventDefault()
                    dropUnassign(e)
                  }}
                >
                  <h2>
                    Waiting · {boardTab === 'women' ? 'Women' : 'Men'} ({filteredUnassigned.length})
                  </h2>
                  <p className="muted waiting-scope">
                    Drag here to unassign · same floor plan on this tab
                    {waitingOtherTab > 0
                      ? ` · ${waitingOtherTab} waiting on the other tab`
                      : ''}
                    .
                  </p>
                  {filteredUnassigned.length === 0 && (
                    <p className="empty">No one waiting on this tab (or filters hide them).</p>
                  )}
                  <ul className="unassigned-list">
                    {filteredUnassigned.map((u) => (
                      <li key={u.contractId} className="chip-row">
                        <button
                          type="button"
                          draggable={editable && !busy}
                          className={chipClassName(
                            selected?.contractId === u.contractId,
                            u.tenantType,
                            u.stayPreference,
                          )}
                          onDragStart={(e) => {
                            dragMovedRef.current = true
                            selectUnassigned(u)
                            beginDrag(u.contractId, e)
                          }}
                          onDragEnd={endDrag}
                          onClick={() => onNameClick(() => selectUnassigned(u))}
                        >
                          {u.firstName} {u.lastName}
                          <span className="muted"> · {u.floorPlanCode}</span>
                        </button>
                        <button
                          type="button"
                          className="chip-info"
                          aria-label={`Details for ${u.firstName} ${u.lastName}`}
                          title="Details"
                          onClick={() => openDetails(u.contractId)}
                        >
                          i
                        </button>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </aside>
          </div>
        </>
      )}

      {appScreen === 'board' && !workspace && !loading && !loadError && termId !== '' && (
        <p className="empty">Make a first plan for this term, or pick an existing working plan above.</p>
      )}

      {appScreen === 'prep' && (
        <section className="app-screen" aria-label="Roommate prep">
          <div className="screen-bar">
            <button type="button" className="screen-back" onClick={backToBoard}>
              ← Back to board
            </button>
            <h2 className="screen-title">Roommate prep</h2>
            <label className="screen-operator">
              Your name
              <input
                value={operator}
                onChange={(e) => setOperator(e.target.value)}
                placeholder="Required for prep actions"
              />
            </label>
            <button
              type="button"
              onClick={() => termId !== '' && reloadPrep(termId)}
              disabled={termId === '' || busy || prepLoading}
            >
              Refresh
            </button>
          </div>
          {termId === '' ? (
            <p className="empty">Pick a term first.</p>
          ) : (
            <>
              <p className="prep-lede">
                Fix names before generating when you can. Original wording stays unchanged.
                {prep
                  ? ` · ${prep.summary.needsAttention} need a look`
                  : prepLoading
                    ? ' · Loading…'
                    : ''}
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
        </section>
      )}

      {appScreen === 'history' && (
        <section className="app-screen" aria-label="Change history">
          <div className="screen-bar">
            <button type="button" className="screen-back" onClick={backToBoard}>
              ← Back to board
            </button>
            <h2 className="screen-title">Change history</h2>
            <label className="screen-operator">
              Your name
              <input
                value={operator}
                onChange={(e) => setOperator(e.target.value)}
                placeholder="Required for undo"
              />
            </label>
            <label className="screen-operator">
              Note
              <input
                value={changeReason}
                onChange={(e) => setChangeReason(e.target.value)}
                placeholder="Optional"
                maxLength={255}
              />
            </label>
            <button
              type="button"
              disabled={draftId === '' || busy || historyLoading}
              onClick={() => draftId !== '' && reloadHistory(draftId)}
            >
              Refresh
            </button>
            <button
              type="button"
              disabled={!editable || busy || !lastUndoable || !operator.trim()}
              onClick={() => onUndo(null)}
            >
              Undo last
            </button>
          </div>
          {draftId === '' ? (
            <p className="empty">Open a working plan on the board first.</p>
          ) : (
            <>
              <p className="muted screen-meta">
                {historyLoading
                  ? 'Loading…'
                  : `${history.length} recent · ${history.filter((h) => h.isUndoable).length} undoable`}
              </p>
              {historyError && (
                <p className="banner bad" role="alert">
                  {historyError}
                </p>
              )}
              {!historyLoading && history.length === 0 && (
                <p className="empty">No recorded changes yet.</p>
              )}
              {history.length > 0 && (
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
            </>
          )}
        </section>
      )}

      {busy && (
        <p className="busy" role="status">
          Working…
        </p>
      )}

      {showFloorPlanCodes && (
        <div
          className="modal-backdrop"
          role="presentation"
          onClick={(e) => {
            if (e.target === e.currentTarget) closeFloorPlanCodes()
          }}
        >
          <div
            className="modal modal-compact"
            role="dialog"
            aria-modal="true"
            aria-labelledby="floor-plan-codes-title"
          >
            <div className="modal-head">
              <h2 id="floor-plan-codes-title">Floor plan codes</h2>
              <button
                type="button"
                className="modal-close"
                onClick={closeFloorPlanCodes}
                autoFocus
              >
                Close
              </button>
            </div>
            <p className="muted modal-lede">
              W = Women’s · M = Men’s. Cap = residents per apartment (2 per bedroom by default).
            </p>
            <ul className="plan-code-list">
              {FLOOR_PLAN_CODES.map((p) => (
                <li key={p.code}>
                  <code>{p.code}</code>
                  <span>
                    {p.category} · {p.shorthand} — {p.name} · cap {p.capacity}
                  </span>
                </li>
              ))}
            </ul>
            <p className="muted modal-hint">Close with Escape or Close.</p>
          </div>
        </div>
      )}

    </main>
  )
}
