---
name: dataverse-plugin-debugging
description: Debug a Dataverse plug-in line by line from a trace-log profiler capture, with the dataverse MCP server's list_profiles, get_profile, debug_profile and debug_step tools and Visual Studio. Use when the user wants to profile, replay, step through or analyze a Dataverse / Dynamics 365 plug-in run.
---

# Debugging a Dataverse plug-in from a profiler capture

The `dataverse` server's tools work on the environment chosen with `select_environment`. A replay calls that environment for real.

## Safety first

- A replay makes every organization-service call the plug-in makes, for real, outside any transaction, as the captured user. Before `allowWrites=true`, tell the user what the plug-in will write and get a clear yes.
- Never replay against a production or client environment. If unsure which environment is selected, call `list_environments` (its `Selected` and `Type` fields) or ask.
- To analyze without writing, use step mode and `debug_step(stop)` before the line that writes (`Create`, `Update`, `Delete`, `Execute`, `Associate`). Calls in `Output` have already happened.
- A capture holds real record data: treat `get_profile` output as sensitive.

## Workflow

1. **Capture.** `list_profiles` shows the flagged steps, the trace-log setting and the captures. With none, `start_profiling(stepId)`; if the trace-log setting is Off, `set_trace_log_setting` and keep the `Previous` value: Exception when the run to debug throws (only failing runs are captured, lighter), All when it succeeds but does the wrong thing. Ask the user to run the operation; the capture appears about 25 seconds later. For an asynchronous step, wait about 3 minutes after changing the setting before the run (the async service picks it up late), and the capture appears once its system job has run.
2. **Read the context.** `get_profile(profileId)`: message, stage, depth, users, InputParameters, images, SharedVariables.
3. **Build.** The plug-in's Debug build with its PDB, open in a running Visual Studio (same elevation as the server).
4. **Start stepping.** `debug_profile(profileId, dllPath, allowWrites=true, stepMode=true)` stops at `Execute` (or at `breakpoints`, comma-separated `file:line` or methods).
5. **Step.** `debug_step(action="over", count=N, evaluate=[...])`. Each step returns its line, source text, changed locals (with return values of the calls on the line), the `evaluate` values after that step, and `Output`: the trace lines and organization-service calls that step caused. `Entity` locals show `logicalname id (n attributes): name=value, ...`. Use `into` / `out` for calls, `continue` to the next breakpoint or the end, `state` to look again.
6. **Finish.** `debug_step(stop)` kills the replay, or `continue` runs it to the end and returns the final result (trace, exception, duration, every call).
7. **Clean up.** `stop_profiling(stepId)` and restore the trace-log setting with `set_trace_log_setting(Previous)`.

## Analysis

Report per line what the plug-in read, decided and wrote: branch conditions with their values, the attributes each call sent, and where it diverged from what the user expected. Put useful expressions in `evaluate` (`context.Depth`, `target.Contains("name")`, `update.Attributes.Count`) instead of re-stepping.

If `debug_profile` says no Visual Studio was found, ask the user to open the plug-in's solution in Visual Studio. One step-mode replay runs at a time.
