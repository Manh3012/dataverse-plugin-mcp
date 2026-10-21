---
name: dataverse-custom-api
description: Create or update a Dataverse Custom API (message, request parameters, response properties, main-operation plug-in) with the dataverse MCP server's register_custom_api, list_custom_apis and register_assembly tools. Use when the user wants a custom message, action or function in Dataverse / Dynamics 365 backed by a plug-in, or wants to change one's parameters.
---

# Building a Custom API

The `dataverse` server's tools work on the environment chosen with `select_environment`.

## Decide before creating

These can't change after creation (a change needs `unregister` + recreate, which breaks callers): `uniqueName`, `bindingType` (Global / Entity / EntityCollection) and `boundEntity`, `isFunction` (Function = GET, needs a response property; otherwise an Action = POST), `allowedStepType`, and each field's `type`, `isOptional` and `logicalEntityName`. Agree them with the user first.

- `uniqueName` must start with the solution publisher's prefix, e.g. `contoso_CalculateDiscount`. It becomes the message name.
- `allowedStepType`: `None` unless others need to extend the API with their own steps.

## Workflow

1. **Main operation code.** Register the plug-in that implements it (`register_assembly`, see the `dataverse-plugin-registration` skill). It reads `context.InputParameters["<param>"]` and sets `context.OutputParameters["<prop>"]`. It needs no step: the Custom API runs it.
2. **Check existing.** `list_custom_apis(uniqueName)`.
3. **Create or update.** `register_custom_api(uniqueName, allowedStepType, bindingType, boundEntity, isFunction, pluginType, requestParameters, responseProperties, solutionUniqueName)`. Fields are JSON arrays, e.g. `[{"uniqueName":"Amount","type":"Decimal","isOptional":false}]`. Types: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference, Float, Integer, Money, Picklist, String, StringArray, Guid.
   - On update, a passed list adds or updates the fields in it; fields left out are kept. To remove one, `unregister(kind="customapiparameter" | "customapiresponse", id)` after the user agrees: callers that send it break.
   - `pluginType` omitted = unchanged, `""` = unlink.
4. **Verify.** `list_custom_apis(uniqueName)`, then give the user a Web API call to test it: Global action `POST [org]/api/data/v9.2/contoso_CalculateDiscount` with the parameters as JSON; bound to an entity `POST .../accounts(<id>)/Microsoft.Dynamics.CRM.contoso_CalculateDiscount`; a function uses `GET` with parameters in the URL.

## Removing

`unregister(kind="customapi", id)` deletes the API with its fields and is refused while other steps are registered on its message. Confirm with the user first: callers break.
