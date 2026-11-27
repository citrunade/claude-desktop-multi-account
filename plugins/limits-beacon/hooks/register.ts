import type { Register, EngineInterface } from 'claude-code'

// Saves the limit numbers each Claude reply already carries, one file per conversation,
// for the Claude Code tracker (which works out each conversation's account).
// No model calls, no tokens.
//
// Near the 5-hour limit (95% by default, set in the tracker's accounts window) it also asks the
// chat to write a short hand-off note, so a new chat on another account can pick up with the
// decisions and state instead of only the raw end of the transcript. The step Claude is in the
// middle of always finishes first; the instruction arrives with that step's result. Until the
// note is saved, only reading and the note itself go through in that turn; once it is saved the
// turn ends. Once per conversation per 5-hour window.
//
// The tracker's hand-off button can also ask for the note: it leaves a request file, this sees
// it within a few seconds and sends the chat a message asking for the note (it waits its turn if
// the chat is busy), with the same holding rules. The tracker hands off once the note lands.
// The installer replaces this with your own folder.
const DIR = 'C:/Users/YOU/.claude/limits-tracker/by-session/'
const ROOT = DIR.replace(/by-session\/?$/, '')
const NOTES = ROOT + 'handoff-notes/'
const SETTINGS = ROOT + 'note-settings.json'
// Tools that only look; Claude may use them while gathering what goes in the note.
const LOOKING = new Set(['Read', 'Grep', 'Glob', 'LS', 'TodoWrite', 'TaskList', 'TaskGet'])
// A button request older than this is from a hand-off the tracker already gave up on.
const REQUEST_MAX_AGE_MS = 5 * 60 * 1000
// If Claude keeps reaching for other tools, stop holding it back after this many refusals.
const MAX_REFUSALS = 4

type Stage = {
  window: string; stage: 'asked' | 'written' | 'gave-up'; turnId?: string; refusals?: number; at: number
  // Why the note was asked for: the 5-hour line, the hand-off button, or a quiet chat. Old files have none.
  why?: 'limit' | 'move' | 'quiet'
  // A button request: its time, and true until the turn it starts has begun.
  requested?: number; pending?: boolean
}

const norm = (p: string) => p.replace(/\\/g, '/').replace(/\/+/g, '/').toLowerCase()

const instruction = (path: string, pct: number) =>
  `This account has used ${pct}% of its 5-hour limit. The step you just ran has finished. ` +
  `Do not start anything new. Now write a hand-off note so a new chat on another account can carry on, then stop.\n\n` +
  `Write it with the Write tool to exactly this file: ${path}\n` +
  `Keep it short (well under 5,000 tokens), in Markdown, with these headings:\n` +
  `- Goal: what the user asked for, in their terms\n` +
  `- Decisions: what was decided and why (include anything the user said to do or not do)\n` +
  `- Done: what is finished and checked\n` +
  `- Half-done: what is partly changed right now and in what state (uncommitted edits, a step stopped midway)\n` +
  `- Open problems: what is broken, unknown or waiting on the user\n` +
  `- Key files: the files and places that matter, one line each\n` +
  `- Next step: the exact next action to take\n\n` +
  `You may read files to get this right, but other tools are held until the note is saved. ` +
  `After saving it, reply with one line saying the note is saved and stop.`

const asked = (path: string, why: string) =>
  (why === 'quiet'
    ? `This chat has been quiet for almost an hour, and Claude's memory of it runs out at one hour. ` +
      `While that is still cheap, write a hand-off note so this work can be picked up later (here or in a new chat), then stop.\n\n`
    : `I'm moving this chat to another account now. Don't start anything new. ` +
      `Write a hand-off note so the new chat can carry on, then stop.\n\n`) +
  `Write it with the Write tool to exactly this file: ${path}\n` +
  `Keep it short (well under 5,000 tokens), in Markdown, with these headings:\n` +
  `- Goal\n- Decisions (and why)\n- Done\n- Half-done\n- Open problems\n- Key files\n- Next step\n\n` +
  `You may read files to get this right, but other tools are held until the note is saved. ` +
  `After saving it, reply with one line saying the note is saved and stop.`

// The reason given when a tool is held. Only the 5-hour stop mentions the limit; a request from the
// tracker (window named 'request-...') never does, even in files written before 'why' was saved.
const heldWhy = (st: Stage) => {
  const why = st.why ?? (st.window.startsWith('request-') ? 'move' : 'limit')
  return why === 'limit' ? 'the 5-hour limit is nearly used up.'
    : why === 'quiet' ? 'this chat has been quiet for almost an hour, so it is writing its hand-off note while that is still cheap.'
    : 'this chat is being moved to another account.'
}

const stageFile = (id: string) => NOTES + id + '.state.json'
const requestFile = (id: string) => NOTES + id + '.request.json'
const notePath = (id: string) => NOTES + id + '.md'

async function readStage($: EngineInterface, id: string): Promise<Stage | undefined> {
  try { return JSON.parse(await $.fs.read(stageFile(id))) } catch { return undefined }
}
async function writeStage($: EngineInterface, id: string, s: Stage) {
  try { await $.fs.write(stageFile(id), JSON.stringify(s)) } catch { }
}
// The tracker left a request (its hand-off button, or this chat being quiet for 55 minutes):
// ask this chat for its note, once per request.
async function checkRequest($: EngineInterface) {
  const id = await $.session.id()
  const req = requestFile(id)
  if (!(await $.fs.exists(req))) return
  let at = 0, why = 'move'
  try {
    const r = JSON.parse(await $.fs.read(req))
    at = Number(r.at) || 0
    if (r.why === 'quiet') why = 'quiet'
  } catch { return }
  const now = await $.clock.now()
  // A quiet-chat request is only worth it before Claude's hour of memory runs out: the tracker asks at
  // 55 minutes, so one picked up late (the chat was asleep) is dropped rather than waking it at full price.
  if (!at || now - at > (why === 'quiet' ? 2 * 60 * 1000 : REQUEST_MAX_AGE_MS)) return
  const st = await readStage($, id)
  if (st?.requested === at) return
  // A window name of its own, so the 5-hour stop can still ask for a fresh note later in this window.
  await writeStage($, id, { window: 'request-' + at, stage: 'asked', refusals: 0, at: now, requested: at, pending: true, why: why as 'move' | 'quiet' })
  await $.prompt.submit({ text: asked(notePath(id), why) })
}

async function settings($: EngineInterface): Promise<{ enabled: boolean; percent: number }> {
  try {
    const s = JSON.parse(await $.fs.read(SETTINGS))
    return { enabled: s.enabled !== false, percent: typeof s.percent === 'number' ? s.percent : 95 }
  } catch { return { enabled: true, percent: 95 } }
}

export const register: Register = (on) => {
  let mainTurn: string | undefined
  // The 5-hour window (its reset time) the latest reading says is at or past the line.
  let due: { window: string; pct: number } | undefined

  on('session.start', async ($, e, next) => {
    const r = await next(e)
    let busy = false
    $.clock.every(3000, async () => {
      if (busy) return
      busy = true
      try { await checkRequest($) } catch { } finally { busy = false }
    })
    return r
  })

  on('turn.start', async ($, e, next) => {
    const r = await next(e)
    if (e.agentId !== undefined) return r
    mainTurn = r?.turnId ?? e.turnId
    // The turn the button's message started: the holding rules apply to it.
    try {
      const id = await $.session.id()
      const st = await readStage($, id)
      if (st?.pending) await writeStage($, id, { ...st, pending: false, turnId: mainTurn })
    } catch { }
    return r
  })

  on('session.measure', async ($, e, next) => {
    const r = await next(e)
    if (e.rateLimits.length === 0) return r
    try {
      const id = await $.session.id()
      await $.fs.write(DIR + id + '.json', JSON.stringify({
        at: await $.clock.now(), sessionId: id, rateLimits: e.rateLimits,
      }))
      const five = e.rateLimits.find(l => l.kind === 'five_hour')
      const s = await settings($)
      if (five && s.enabled && five.percentUsed >= s.percent) {
        const window = five.resetsAt ?? 'unknown'
        const st = await readStage($, id)
        due = st && st.window === window ? undefined : { window, pct: Math.floor(five.percentUsed) }
      } else due = undefined
    } catch { /* never get in the way of the session */ }
    return r
  })

  on('tool.call', async ($, e, next) => {
    if (e.agentId !== undefined) return next(e)   // a subagent's own steps: the main chat decides
    let id: string
    try { id = await $.session.id() } catch { return next(e) }

    // 1. Just crossed the line: let this step finish, then hand Claude the instruction with its result.
    if (due) {
      const d = due; due = undefined
      const st = await readStage($, id)
      if (!st || st.window !== d.window) {
        const r = await next(e)
        await writeStage($, id, { window: d.window, stage: 'asked', turnId: mainTurn, refusals: 0, at: await $.clock.now(), why: 'limit' })
        try { $.ui.toast(`5-hour limit at ${d.pct}%: Claude is writing a hand-off note, then stopping.`) } catch { }
        const say = instruction(notePath(id), d.pct)
        if (r.deny !== undefined) return { deny: r.deny + '\n\n' + say }
        return { ...r, context: [...(r.context ?? []), say] }
      }
    }

    const st = await readStage($, id)
    if (!st || st.turnId !== mainTurn) return next(e)   // a later turn: the user chose to carry on

    const toNote = (e.tool === 'Write' || e.tool === 'Edit') &&
      norm(String((e as any).file_path ?? '')) === norm(notePath(id))

    // 2. Asked, note not saved yet: the note and looking around go through, other steps wait.
    if (st.stage === 'asked') {
      if (toNote) {
        const r = await next(e)
        if (r.deny === undefined && !r.isError) {
          await writeStage($, id, { ...st, stage: 'written', at: await $.clock.now() })
          try { $.ui.toast('Hand-off note saved. This chat has stopped; the tracker uses the note when you move it.') } catch { }
          return { ...r, context: [...(r.context ?? []), 'The hand-off note is saved. Reply with one line saying so and stop now; do not run any more tools.'] }
        }
        return r
      }
      if (LOOKING.has(e.tool)) return next(e)
      const refusals = (st.refusals ?? 0) + 1
      if (refusals > MAX_REFUSALS) {
        await writeStage($, id, { ...st, stage: 'gave-up', refusals })
        return next(e)
      }
      await writeStage($, id, { ...st, refusals })
      return { deny: `Held: ${heldWhy(st)} Write the hand-off note to ${notePath(id)} with the Write tool first, then stop.` }
    }

    // 3. Note saved but Claude reached for another tool in the same turn: end the turn.
    if (st.stage === 'written' && !toNote && !LOOKING.has(e.tool)) {
      if (mainTurn) { try { await $.turn.abort({ turnId: mainTurn }) } catch { } }
      return { deny: 'The hand-off note is saved and this chat is stopping. Do not run more tools.' }
    }
    return next(e)
  })
}
