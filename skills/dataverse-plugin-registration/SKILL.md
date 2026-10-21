---
name: dataverse-plugin-registration
description: Register or update a Dataverse plug-in end to end with the dataverse MCP server's tools (assembly or plug-in package, steps, pre/post images, enable/disable, solution membership and unregistering). Use when the user wants to deploy, register, update, add a step or image to, disable or remove a Dataverse / Dynamics 365 plug-in (what the Plugin Registration Tool does).
---

# Registering a Dataverse plug-in

The `dataverse` server's tools work on the environment chosen with `select_environment`. Check `list_environments` (`Selected`, `Type`) before writing, and confirm with the user before registering into a Production environment.

## Workflow

1. **Build.** Build the plug-in project (Release). A classic project gives a signed `.dll`; a `pac plugin init` project gives a `.nupkg`.
2. **Inspect locally.** `inspect_assembly(dllPath)` or `inspect_package(nupkgPath)`: identity, signing, and the IPlugin / CodeActivity classes. An unsigned DLL can't be registered as a classic assembly.
3. **See what is there.** `list_registrations(assemblyName)` shows the assembly → type → step → image tree and its solutions. Note step ids and names before changing anything.
4. **Register the code.**
   - DLL: `register_assembly(dllPath, solutionUniqueName)`. Same major.minor updates in place (skipped when unchanged). A different major.minor is refused unless the user wants a side-by-side assembly (`registerSideBySide=true`, steps must then be recreated on the new types).
   - Package: `register_package(nupkgPath, solutionUniqueName)`. The solution is required the first time (its publisher prefix names the package). Packages can't hold custom workflow activities.
5. **Register steps.** `register_step(message, stage, pluginType, mode, primaryEntity, filteringAttributes, ..., solutionUniqueName)`. Calling it again updates the step: matched by `name`, or without a name by handler + message + `primaryEntity`. On update, omitted fields (stage, mode, entity, ...) stay unchanged; pass only what changes.
   - On update, pass only what changes: omitted fields stay as they are, and an empty string clears one (`filteringAttributes=""`, `primaryEntity=""` = any entity). The step is found by name, so pass the existing `name`.
   - Asynchronous needs PostOperation. For Update, always set `filteringAttributes` to the columns the plug-in reacts to.
6. **Register images.** `register_image(stepId, alias, imageType, attributes)` with only the columns the plug-in reads. Empty `attributes` captures every column: avoid.
7. **Verify.** `list_registrations` again, then ask the user to trigger the operation. If it fails, use the `dataverse-plugin-troubleshooting` skill.

## Changing and removing

- `set_step_state(stepId, enabled)` disables a step without losing its registration; prefer it over unregistering while investigating.
- `unregister(kind, id)` deletes for good and is refused while dependants exist: remove images → steps → types → assembly/package in that order. Confirm each delete with the user, naming what will go.
- Steps and images must be added to the solution too (`solutionUniqueName` on `register_step`), not only the assembly, or they are missing when the solution is exported.

## Report

Show the user a short tree of what was created or updated (assembly, types, steps with stage/mode/entity, images with their columns) and the ids.
