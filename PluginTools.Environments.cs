using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Environment picker: Dataverse.ListEnvironments / SelectEnvironment in Program.cs.
public static partial class PluginTools
{
    [McpServerTool(Name = "list_environments", ReadOnly = true)]
    [Description("List the Dataverse environments the signed-in account can access (Global Discovery Service, commercial cloud), with Type (Production, Sandbox, Developer...) and which one is selected. " +
        "The first call opens the browser to sign in; the sign-in is cached across sessions. To switch environments, call select_environment without a url instead: the user picks.")]
    public static string ListEnvironments(Dataverse dv) => Run(() => dv.ListEnvironments().GetAwaiter().GetResult());

    [McpServerTool(Name = "select_environment")]
    [Description("Connect every other tool to an environment and remember it for later sessions. Call it without url: it shows the user a picker of their environments, so the user chooses, not you " +
        "(the first call opens the browser to sign in). Pass url only when the user named the environment. debug_profile replays write to the selected environment.")]
    public static async Task<string> SelectEnvironment(Dataverse dv, McpServer server,
        [Description("Environment Url the user named, e.g. https://yourorg.crm.dynamics.com. Omit to show the picker")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        if (url == null)
        {
            List<DataverseEnvironment> environments;
            try { environments = await dv.ListEnvironments(); }
            catch (Exception e) when (e is not McpException) { throw new McpException(e.Message, e); }
            var choices = environments.Where(e => !e.Disabled).ToList();
            if (choices.Count == 0) throw new McpException("The signed-in account has no enabled Dataverse environments.");
            // No picker UI: Claude asks the user instead.
            var askUser = Run(() => new { Note = "No picker was shown, or the user closed it. Ask the user which environment to use with AskUserQuestion (one option per environment, Name as the label, Type and host as the description; past four options the user picks through Other), then call select_environment with its Url.", Environments = choices });
            if (server.ClientCapabilities?.Elicitation == null) return askUser;
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Pick the Dataverse environment the plug-in tools connect to. debug_profile replays write to it, so prefer a dev or sandbox one.",
                RequestedSchema = new()
                {
                    Properties =
                    {
                        ["url"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                        {
                            Title = "Environment",
                            OneOf = choices.Select(e => new ElicitRequestParams.EnumSchemaOption { Const = e.Url, Title = $"{e.Name} ({e.Type}){(e.Selected ? ", current" : "")}" }).ToList(),
                            Default = choices.FirstOrDefault(e => e.Selected)?.Url,
                        },
                    },
                    Required = ["url"],
                },
            }, cancellationToken);
            // The desktop Code tab advertises elicitation but declines the form unseen, which looks the same as the user closing it.
            if (!answer.IsAccepted || answer.Content?.TryGetValue("url", out var picked) != true) return askUser;
            url = picked.GetString()!;
        }
        return Run(() => dv.SelectEnvironment(url));
    }
}
