import { atom, read, update } from 'claude-code'
import type { Engine, Register } from 'claude-code'

import type { Choice, Environment } from '../types'

// The band above the prompt: which Dataverse environment the dataverse server's tools use.
// select_environment saves it (Program.cs, Dataverse.SelectEnvironment): line 1 the URL, line 2 the name.
const current = atom({ plugin: 'dataverse-plugin-tools', key: 'environment' } as const, null)
// The environments the band's picker shows while it is open; null while closed.
const picking = atom({ plugin: 'dataverse-plugin-tools', key: 'picking' } as const, null)
const TOOLS = 'mcp__plugin_dataverse-plugin-tools_dataverse__'

async function refresh($: Engine) {
  const home = await $.env.get('LOCALAPPDATA')
  // Missing until the first select_environment: the read rejects.
  const text = home ? await $.fs.read(`${home}\\DataversePluginMcp\\environment.txt`).catch(() => '') : ''
  await update($, current, () => parseEnvironment(text))
}

export function parseEnvironment(text: string): Environment | null {
  const [url = '', name = ''] = text.split(/\r?\n/).map(line => line.trim())
  return url ? { url, name } : null
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    await refresh($)
    return next(e)
  })

  // shortcut: re-read after every turn rather than matching select_environment's tool name; one small file read.
  on('turn.complete', async ($, e, next) => {
    await refresh($)
    return next(e)
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)
    const environment = await read($, current)
    const choices = await read($, picking)
    const ui = $.ui.resolve(e)
    const { Box, Text, Button } = ui
    // Stack on whatever the plugins beneath draw, rather than replacing their band.
    const below = await next(e)
    const host = environment?.url.replace('https://', '')

    // The server's own picker is an MCP form the desktop declines unseen, so the band draws it, calling the tools itself: no model turn.
    const call = async (tool: string, args: object) => {
      const r = await $.tool.call({ tool: TOOLS + tool, ...args } as never)
      if (r.deny !== undefined || r.isError) throw new Error(r.deny ?? r.text ?? `${tool} failed`)
      return r.text ?? ''
    }
    const fail = (err: unknown) => $.ui.toast(`Dataverse: ${err instanceof Error ? err.message : String(err)}`)
    const openPicker = () =>
      call('list_environments', {})
        .then(text => update($, picking, () => (JSON.parse(text) as Choice[]).filter(c => !c.Disabled)))
        .catch(fail)
    const pick = (url: string) =>
      call('select_environment', { url })
        .then(() => refresh($))
        .catch(fail)
        .finally(() => update($, picking, () => null))
    const closePicker = () => update($, picking, () => null)
    // The mobile app draws no Select: there the model asks instead.
    const switchEnvironment =
      'Select' in ui ? openPicker : () => void $.prompt.submit({ text: 'Switch the Dataverse environment with select_environment.' })

    if (choices && 'Select' in ui) {
      const { Select } = ui
      return (
        <Box flexDirection="column">
          <Box flexDirection="row" alignItems="center" gap={2} width={e.props.bodyColumns}>
            <Text>
              <Text color="suggestion">●</Text> Dataverse
            </Text>
            <Select
              key="environment"
              autoFocus
              options={choices.map(c => ({ value: c.Url, label: `${c.Name} (${c.Type})` }))}
              value={choices.find(c => c.Selected)?.Url}
              onSelect={pick}
            />
            <Box flexGrow={1} />
            <Button key="cancel" plain label="Cancel" onPress={closePicker} />
          </Box>
          {below}
        </Box>
      )
    }

    // One row: a status dot, the facts, and a button at the right.
    return (
      <Box flexDirection="column">
        <Box flexDirection="row" alignItems="center" gap={2} width={e.props.bodyColumns}>
          <Text>
            <Text color={environment ? 'suggestion' : 'warning'}>●</Text> Dataverse
          </Text>
          {environment ? (
            <Text wrap="truncate-end">
              <Text bold>{environment.name || host}</Text>
              {environment.name ? <Text dimColor> {host}</Text> : null}
            </Text>
          ) : (
            <Text color="warning">no environment selected</Text>
          )}
          <Box flexGrow={1} />
          <Button key="switch" plain label={environment ? 'Switch' : 'Select'} onPress={switchEnvironment} />
        </Box>
        {below}
      </Box>
    )
  })
}
