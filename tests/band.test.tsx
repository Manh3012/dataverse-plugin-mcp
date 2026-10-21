import { expect, mock, test } from 'claude-code/testing'

import { parseEnvironment } from '../hooks/register'

const BAND = { plugin: 'dataverse-plugin-tools', component: 'AbovePrompt', props: { hasSurvey: false, isWorking: false, maxRows: 10 } } as const

test('the saved file becomes the environment', () => {
  const contoso = parseEnvironment('https://contoso.crm5.dynamics.com\r\nContoso Dev\r\n')
  expect(contoso).toEqual({ url: 'https://contoso.crm5.dynamics.com', name: 'Contoso Dev' })
  // A file from before the name was saved: the URL alone.
  expect(parseEnvironment('https://org.crm.dynamics.com\n')).toEqual({ url: 'https://org.crm.dynamics.com', name: '' })
  expect(parseEnvironment('')).toBe(null)
})

test('with no saved environment, the band says none is selected', async ($, on) => {
  mock.env(on, { LOCALAPPDATA: 'C:\\Users\\me\\AppData\\Local' })
  on('fs.read', async () => { throw new Error('ENOENT') })
  on('session.start', async ($, e) => ({ cwd: e.cwd }))
  // What the plugins beneath draw: kept under the line, not replaced.
  on('ui.render', async ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>beneath</Text>
  })

  for (const surface of ['terminal', 'desktop'] as const) {
    await $.session.start({ cwd: 'C:\\repo', surface } as never)
    const ui = await $.ui.mount({ ...BAND, surface })
    expect(await ui.find({ type: 'Text', text: /no environment selected/ })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: 'beneath' })).toBeDefined()
    await ui.unmount()
  }
})

test('a saved environment draws as one row with a Switch button, above what the plugins beneath draw', async ($, on) => {
  mock.env(on, { LOCALAPPDATA: 'C:\\Users\\me\\AppData\\Local' })
  on('fs.read', async () => ({ value: 'https://contoso.crm5.dynamics.com\r\nContoso Dev\r\n' }))
  on('session.start', async ($, e) => ({ cwd: e.cwd }))
  on('ui.render', async ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>beneath</Text>
  })

  for (const surface of ['terminal', 'desktop'] as const) {
    await $.session.start({ cwd: 'C:\\repo', surface } as never)
    const ui = await $.ui.mount({ ...BAND, surface })
    expect(await ui.find({ type: 'Button', props: { label: 'Switch' } })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: 'Contoso Dev' })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: 'beneath' })).toBeDefined()
    await ui.unmount()
  }
})

test('Switch opens a picker in the band, and a pick selects that environment without a model turn', async ($, on) => {
  const dev = 'https://fabrikam-dev.crm5.dynamics.com'
  let saved = 'https://contoso.crm5.dynamics.com\r\nContoso Dev\r\n'
  const selected: string[] = []
  mock.env(on, { LOCALAPPDATA: 'C:\\Users\\me\\AppData\\Local' })
  on('fs.read', async () => ({ value: saved }))
  on('session.start', async ($, e) => ({ cwd: e.cwd }))
  on('ui.render', async ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>beneath</Text>
  })
  // Stands for the dataverse server's tools.
  on('tool.call', async ($, e) => {
    const args = e as unknown as { tool: string; url?: string }
    if (args.tool.endsWith('__list_environments'))
      return {
        result: '',
        text: JSON.stringify([
          { Name: 'Contoso Dev', Url: 'https://contoso.crm5.dynamics.com', Type: 'Developer', Disabled: false, Selected: true },
          { Name: 'Fabrikam Dev', Url: dev, Type: 'Developer', Disabled: false, Selected: false },
          { Name: 'Old', Url: 'https://old.crm5.dynamics.com', Type: 'Sandbox', Disabled: true, Selected: false },
        ]),
      } as never
    selected.push(args.url ?? '')
    saved = `${dev}\r\nFabrikam Dev\r\n`
    return { result: '', text: '{}' } as never
  })

  for (const surface of ['terminal', 'desktop'] as const) {
    await $.session.start({ cwd: 'C:\\repo', surface } as never)
    const ui = await $.ui.mount({ ...BAND, surface })
    await ui.press({ key: 'switch' })
    const picker = await ui.find({ key: 'environment' })
    // A disabled environment is not offered.
    expect(JSON.stringify(picker)).not.toContain('old.crm5')
    await ui.select({ key: 'environment', value: dev })
    expect(selected.at(-1)).toBe(dev)
    expect(await ui.find({ type: 'Text', text: 'Fabrikam Dev' })).toBeDefined()
    expect(await ui.find({ key: 'environment' })).toBeUndefined()
    await ui.unmount()
  }
})
