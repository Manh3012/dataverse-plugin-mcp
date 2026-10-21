export type Environment = { url: string; name: string }
// One row of list_environments, as the band's picker shows it.
export type Choice = { Name: string; Url: string; Type: string; Disabled: boolean; Selected: boolean }

declare module 'claude-code' {
  interface PluginState {
    'dataverse-plugin-tools': { environment: Environment | null; picking: Choice[] | null }
  }
}
