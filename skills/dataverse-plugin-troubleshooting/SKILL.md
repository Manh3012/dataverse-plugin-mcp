---
name: dataverse-plugin-troubleshooting
description: Find out why a Dataverse plug-in failed, didn't fire, or ran too often, from the Plug-in Trace Log and the registrations, with the dataverse MCP server's get_trace_logs, set_trace_log_setting, list_registrations and set_step_state tools. Use when the user reports a plug-in error, a save that fails with a business process error, an infinite loop / depth error, or a plug-in that does nothing.
---

# Troubleshooting a Dataverse plug-in

Work read-only first. The only writes here are the trace-log setting and step state, and both must be put back.

## Workflow

1. **Is it registered as expected?** `list_registrations(assemblyName)`: the step's message, entity, stage, mode, `filteringAttributes`, enabled state, images and their columns. Many "does nothing" reports end here: a disabled step, a wrong entity, filtering attributes that don't include the changed column, or an image without the column the code reads.
2. **Is tracing on?** `get_trace_logs` also returns the trace setting. If it is Off, `set_trace_log_setting(Exception)` (or `All` to see successful runs too), keep the `Previous` value, and ask the user to reproduce.
3. **Read the logs.** `get_trace_logs(typeName, messageName, primaryEntity, onlyExceptions, sinceMinutes)`. Rows appear a few seconds after the run and are purged after about 24 hours. The trace output (`messageblock`) is only returned to System Administrators.
4. **Follow one request.** `get_trace_logs(correlationId)` returns the whole pipeline chain of that request: which plug-ins ran, in what order and depth, and which one threw.
5. **Restore.** `set_trace_log_setting(Previous)`. Never leave All on in production: it costs storage.

## Reading the evidence

- **Exception:** quote the exception type and message and the last trace lines before it. Map the stack trace to the plug-in source if the project is open.
- **Depth / loop:** the same type at rising depth in one correlation chain means the plug-in updates its own trigger. The fix is `filteringAttributes`, a `context.Depth` guard, or only writing changed columns.
- **Timeouts:** synchronous plug-ins have a 2-minute limit; long durations in the rows point to slow queries or external calls.
- **Not firing:** no rows at All after a reproduction means the step didn't match (see step 1), not that the code is silent.

To stop a broken step while the user fixes it, `set_step_state(stepId, false)`, after the user agrees, and re-enable it afterwards. To step through the failing run line by line, use the `dataverse-plugin-debugging` skill.

Trace rows hold real record data: quote only what the diagnosis needs.
