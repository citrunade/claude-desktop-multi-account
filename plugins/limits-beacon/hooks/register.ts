import type { Register } from 'claude-code'

// Saves the limit numbers each Claude reply already carries, one file per conversation,
// for the Claude Code tracker (which works out each conversation's account).
// No model calls, no tokens.
// The installer replaces this with your own folder.
const DIR = 'C:/Users/YOU/.claude/limits-tracker/by-session/'

export const register: Register = (on) => {
  on('session.measure', async ($, e, next) => {
    const r = await next(e)
    if (e.rateLimits.length === 0) return r
    try {
      const id = await $.session.id()
      await $.fs.write(DIR + id + '.json', JSON.stringify({
        at: await $.clock.now(), sessionId: id, rateLimits: e.rateLimits,
      }))
    } catch { /* never get in the way of the session */ }
    return r
  })
}
