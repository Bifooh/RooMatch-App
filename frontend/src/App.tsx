import { useEffect, useState } from 'react'

type Health = { status: string; database?: string; serverVersion?: string; error?: string }
type Summary = { tenants: number; apartments: number; floorPlans: number }

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url)
  const body = await res.json().catch(() => null)
  if (!res.ok) throw new Error(body?.error ?? `${url} returned ${res.status}`)
  return body as T
}

export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [summary, setSummary] = useState<Summary | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getJson<Health>('/api/health')
      .then((h) => { setHealth(h); return getJson<Summary>('/api/summary') })
      .then(setSummary)
      .catch((e: Error) => setError(e.message))
  }, [])

  return (
    <main>
      <h1>Lodge tenant assignment (MVP)</h1>
      <p><em>Fictional data only.</em></p>
      {error && <p className="bad">Error: {error}</p>}
      {health && (
        <p className={health.status === 'ok' ? 'ok' : 'bad'}>
          API: {health.status}{health.database && ` (database ${health.database}, MySQL ${health.serverVersion})`}
        </p>
      )}
      {!health && !error && <p>Checking API…</p>}
      {summary && (
        <table>
          <tbody>
            <tr><th>Tenants</th><td>{summary.tenants}</td></tr>
            <tr><th>Apartments</th><td>{summary.apartments}</td></tr>
            <tr><th>Floor plans</th><td>{summary.floorPlans}</td></tr>
          </tbody>
        </table>
      )}
    </main>
  )
}
