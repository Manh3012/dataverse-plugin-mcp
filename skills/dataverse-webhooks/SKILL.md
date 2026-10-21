---
name: dataverse-webhooks
description: Send Dataverse events to a webhook, Azure Service Bus queue / topic or Event Hub, with the dataverse MCP server's register_service_endpoint, list_service_endpoints, register_step and register_image tools. Use when the user wants Dataverse / Dynamics 365 to call an HTTP endpoint or push messages to Azure when records are created, updated or deleted.
---

# Webhooks and Service Bus endpoints

The `dataverse` server's tools work on the environment chosen with `select_environment`.

## Secrets

- Ask the user for the webhook key, header values or connection string; never invent or log them. They are write-only: `list_service_endpoints` only says whether one is set, and omitting them on update keeps the stored value.
- Prefer a Service Bus SAS key scoped to Send on one queue / topic over a namespace root key.

## Workflow

1. **Check existing.** `list_service_endpoints(name)`.
2. **Create or update the endpoint.** `register_service_endpoint(name, contract, ...)`. `contract` can't change later.
   - Webhook: `url` (http/https, port 80 or 443), `authType` = WebhookKey (`authValue` = the key, sent as `?code=`, fits Azure Functions) | HttpHeader | HttpQueryString (`authValue` = JSON object of name/value pairs).
   - Queue / Topic / EventHub: `connectionString` (`Endpoint=sb://...;SharedAccessKeyName=..;SharedAccessKey=..;EntityPath=..`), `messageFormat` Json | TextXml | BinaryXml (not for EventHub).
   - `userClaim` UserId / UserInfo adds the caller to the message (Service Bus / Event Hub only; webhooks take None).
3. **Register the step.** `register_step(message, stage, serviceEndpoint=<name>, primaryEntity, filteringAttributes, mode, solutionUniqueName)`.
   - Service Bus / Event Hub steps must be PostOperation + Asynchronous.
   - Endpoint steps in PostOperation default to Asynchronous. A webhook step can be made synchronous (`mode="Synchronous"`), but then a failing or slow endpoint (2-minute limit) fails the user's save: only on the user's explicit request.
   - For Update, set `filteringAttributes` so unrelated edits don't fire it.
4. **Images.** `register_image(stepId, alias, imageType, attributes)` puts the record's before/after values into the posted context.
5. **Verify.** Ask the user to trigger the event. The receiver gets the serialized execution context (JSON `RemoteExecutionContext`). Failed asynchronous deliveries show as failed system jobs in Dataverse.

## Removing

Unregister the steps first (`unregister(kind="step", id)`), then the endpoint (`unregister(kind="endpoint", id)`); it is refused while steps use it. Confirm with the user first.
