﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.ExtensionBundle;
using Azure.Functions.Cli.Extensions;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Colors.Net;
using Fclp;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Azure.Functions.Cli.Common.Constants;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Actions.LocalActions
{
    [Action(Name = "new", Context = Context.Function, HelpText = "Create a new function from a template.")]
    [Action(Name = "new", HelpText = "Create a new function from a template.")]
    [Action(Name = "create", Context = Context.Function, HelpText = "Create a new function from a template.")]
    internal class CreateFunctionAction : BaseAction
    {
        private ITemplatesManager _templatesManager;
        private readonly ISecretsManager _secretsManager;
        private readonly IContextHelpManager _contextHelpManager;

        private readonly InitAction _initAction;
        public WorkerRuntime workerRuntime;

        public string Language { get; set; }
        public string TemplateName { get; set; }
        public string FunctionName { get; set; }
        public bool Csx { get; set; }
        private string TriggerNameForHelp { get; set; }
        private string FileName { get; set; }
        private string Route { get; set; }
        public AuthorizationLevel? AuthorizationLevel { get; set; }

        Lazy<IEnumerable<Template>> _templates;
        Lazy<IEnumerable<NewTemplate>> _newTemplates;
        Lazy<IEnumerable<UserPrompt>> _userPrompts;
        IDictionary<string, string> _newTemplateLabelMap;


        public CreateFunctionAction(ITemplatesManager templatesManager, ISecretsManager secretsManager, IContextHelpManager contextHelpManager)
        {
            _templatesManager = templatesManager;
            _secretsManager = secretsManager;
            _contextHelpManager = contextHelpManager;
            _initAction = new InitAction(_templatesManager, _secretsManager);
            _templates = new Lazy<IEnumerable<Template>>(() => { return _templatesManager.Templates.Result; });
            _newTemplates = new Lazy<IEnumerable<NewTemplate>>(() => { return _templatesManager.NewTemplates.Result; });
            _userPrompts = new Lazy<IEnumerable<UserPrompt>>(() => { return _templatesManager.UserPrompts.Result; });
            _newTemplateLabelMap = CreateLabelMap();
        }

        public override ICommandLineParserResult ParseArgs(string[] args)
        {
            Parser
                .Setup<string>('l', "language")
                .WithDescription($"Template programming language, such as C#, F#, JavaScript, etc.")
                .Callback(l => Language = l);

            Parser
                .Setup<string>('t', "template")
                .WithDescription("Template name")
                .Callback(t => TemplateName = t);

            Parser
                .Setup<string>('n', "name")
                .WithDescription("Function name")
                .Callback(n => FunctionName = n);

            Parser
                .Setup<string>('f', "file")
                .WithDescription("File Name")
                .Callback(f => FileName = f);

            Parser
                .Setup<string>('r', "route")
                .WithDescription("Route")
                .Callback(f => Route = f);

            Parser
                .Setup<AuthorizationLevel?>('a', "authlevel")
                .WithDescription("Authorization level is applicable to templates that use Http trigger, Allowed values: [function, anonymous, admin]. Authorization level is not enforced when running functions from core tools")
                .Callback(a => AuthorizationLevel = a);

            Parser
                .Setup<bool>("csx")
                .WithDescription("use old style csx dotnet functions")
                .Callback(csx => Csx = csx);

            _initAction.ParseArgs(args);

            ParseTriggerForHelpRequest(args);
            return base.ParseArgs(args);
        }

        public async override Task RunAsync()
        {
            // Check if the command only ran for help. 
            if (!string.IsNullOrEmpty(TriggerNameForHelp))
            {
                await ProcessHelpRequest(TriggerNameForHelp, true);
                return;
            }

            if (!ValidateInputs())
            {
                return;
            }

            await UpdateLanguageAndRuntime();

            if (WorkerRuntimeLanguageHelper.IsDotnet(workerRuntime) && !Csx)
            {
                SelectionMenuHelper.DisplaySelectionWizardPrompt("template");
                TemplateName = TemplateName ?? SelectionMenuHelper.DisplaySelectionWizard(DotnetHelpers.GetTemplates(workerRuntime));
                ColoredConsole.Write("Function name: ");
                FunctionName = FunctionName ?? Console.ReadLine();
                ColoredConsole.WriteLine(FunctionName);
                var namespaceStr = Path.GetFileName(Environment.CurrentDirectory);
                await DotnetHelpers.DeployDotnetFunction(TemplateName.Replace(" ", string.Empty), Utilities.SanitizeClassName(FunctionName), Utilities.SanitizeNameSpace(namespaceStr), Language.Replace("-isolated", ""), workerRuntime, AuthorizationLevel);
            }
            else if (IsNewPythonProgrammingModel())
            {
                if (string.IsNullOrEmpty(TemplateName))
                {
                    SelectionMenuHelper.DisplaySelectionWizardPrompt("template");
                    TemplateName = TemplateName ?? SelectionMenuHelper.DisplaySelectionWizard(GetTriggerNamesFromNewTemplates(Language));
                }

                var template = _newTemplates.Value.FirstOrDefault(t => string.Equals(t.Name, TemplateName, StringComparison.CurrentCultureIgnoreCase) && string.Equals(t.Language, Language, StringComparison.CurrentCultureIgnoreCase));
                var templateJob = template.Jobs.Single(x => x.Input.UserCommand.Equals("appendToFile", StringComparison.OrdinalIgnoreCase));
                var actionNames = templateJob.Actions;
                var actions = template.Actions.Where(x => actionNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var variables = new Dictionary<string, string>();
                RunUserInputActions(actionNames, actions, variables);
                await _templatesManager.Deploy(FileName, templateJob, template, variables);
            }
            else
            {
                SelectionMenuHelper.DisplaySelectionWizardPrompt("template");
                string templateLanguage;
                try
                {
                    templateLanguage = WorkerRuntimeLanguageHelper.NormalizeLanguage(Language);
                }
                catch (Exception)
                {
                    // Ideally this should never happen.
                    templateLanguage = WorkerRuntimeLanguageHelper.GetDefaultTemplateLanguageFromWorker(workerRuntime);
                }

                TelemetryHelpers.AddCommandEventToDictionary(TelemetryCommandEvents, "language", templateLanguage);
                TemplateName = TemplateName ?? SelectionMenuHelper.DisplaySelectionWizard(GetTriggerNames(templateLanguage));
                ColoredConsole.WriteLine(TitleColor(TemplateName));

                Template template = GetLanguageTemplates(templateLanguage).FirstOrDefault(t => Utilities.EqualsIgnoreCaseAndSpace(t.Metadata.Name, TemplateName));

                if (template == null)
                {
                    TelemetryHelpers.AddCommandEventToDictionary(TelemetryCommandEvents, "template", "N/A");
                    throw new CliException($"Can't find template \"{TemplateName}\" in \"{Language}\"");
                }
                else
                {
                    TelemetryHelpers.AddCommandEventToDictionary(TelemetryCommandEvents, "template", TemplateName);

                    var extensionBundleManager = ExtensionBundleHelper.GetExtensionBundleManager();
                    if (template.Metadata.Extensions != null && !extensionBundleManager.IsExtensionBundleConfigured() && !CommandChecker.CommandExists("dotnet"))
                    {
                        throw new CliException($"The {template.Metadata.Name} template has extensions. {Constants.Errors.ExtensionsNeedDotnet}");
                    }

                    if (!IsNewNodeJsProgrammingModel(workerRuntime) && AuthorizationLevel.HasValue)
                    {
                        ConfigureAuthorizationLevel(template);
                    }

                    ColoredConsole.Write($"Function name: [{template.Metadata.DefaultFunctionName}] ");
                    FunctionName = FunctionName ?? Console.ReadLine();
                    FunctionName = string.IsNullOrEmpty(FunctionName) ? template.Metadata.DefaultFunctionName : FunctionName;
                    await _templatesManager.Deploy(FunctionName, FileName, template);
                    PerformPostDeployTasks(FunctionName, Language);
                }
            }
            ColoredConsole.WriteLine($"The function \"{FunctionName}\" was created successfully from the \"{TemplateName}\" template.");
            if (string.Equals(Language, Languages.Python, StringComparison.CurrentCultureIgnoreCase) && !IsNewPythonProgrammingModel())
            {
                PythonHelpers.PrintPySteinAwarenessMessage();
            }

            var isNewNodeJsModel = IsNewNodeJsProgrammingModel(workerRuntime);
            if (workerRuntime == WorkerRuntime.node && !isNewNodeJsModel)
            {
                NodeJSHelpers.PrintV4AwarenessMessage();
            }
        }

        public bool ValidateInputs()
        {
            if (Console.IsOutputRedirected || Console.IsInputRedirected)
            {
                if (string.IsNullOrEmpty(TemplateName) ||
                    string.IsNullOrEmpty(FunctionName))
                {
                    ColoredConsole
                        .Error
                        .WriteLine(ErrorColor("Running with stdin\\stdout redirected. Command must specify --template, and --name explicitly."))
                        .WriteLine(ErrorColor("See 'func help function' for more details"));
                    return false;
                }
            }

            return true;
        }

        public async Task UpdateLanguageAndRuntime()
        {
            workerRuntime = GlobalCoreToolsSettings.CurrentWorkerRuntimeOrNone;
            if (!CurrentPathHasLocalSettings())
            {
                // we're assuming "func init" has not been run
                await _initAction.RunAsync();
                workerRuntime = _initAction.ResolvedWorkerRuntime;
                Language = _initAction.ResolvedLanguage;
            }


            if (workerRuntime != WorkerRuntime.None && !string.IsNullOrWhiteSpace(Language))
            {
                // validate
                var workerRuntimeSelected = WorkerRuntimeLanguageHelper.NormalizeWorkerRuntime(Language);
                if (workerRuntime != workerRuntimeSelected)
                {
                    throw new CliException("Selected language doesn't match worker set in local.settings.json." +
                        $"Selected worker is: {workerRuntime} and selected language is: {workerRuntimeSelected}");
                }
            }
            else if (string.IsNullOrWhiteSpace(Language))
            {
                if (workerRuntime == WorkerRuntime.None)
                {
                    SelectionMenuHelper.DisplaySelectionWizardPrompt("language");
                    Language = SelectionMenuHelper.DisplaySelectionWizard(_templates.Value.Select(t => t.Metadata.Language).Where(l => !l.Equals("python", StringComparison.OrdinalIgnoreCase)).Distinct());
                    workerRuntime = WorkerRuntimeLanguageHelper.SetWorkerRuntime(_secretsManager, Language);
                }
                else if (!WorkerRuntimeLanguageHelper.IsDotnet(workerRuntime) || Csx)
                {
                    var languages = WorkerRuntimeLanguageHelper.LanguagesForWorker(workerRuntime);
                    var displayList = _templates.Value
                            .Select(t => t.Metadata.Language)
                            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    if (displayList.Length == 1)
                    {
                        Language = displayList.First();
                    }
                    else if (!InferAndUpdateLanguage(workerRuntime))
                    {
                        SelectionMenuHelper.DisplaySelectionWizardPrompt("language");
                        Language = SelectionMenuHelper.DisplaySelectionWizard(displayList);
                    }
                }
                else if (WorkerRuntimeLanguageHelper.IsDotnet(workerRuntime))
                {
                    InferAndUpdateLanguage(workerRuntime);
                }
            }
            else if (!string.IsNullOrWhiteSpace(Language))
            {
                workerRuntime = WorkerRuntimeLanguageHelper.SetWorkerRuntime(_secretsManager, Language);
            }
        }
        private IEnumerable<string> GetTriggerNames(string templateLanguage, bool forNewModelHelp = false)
        {
            return GetLanguageTemplates(templateLanguage, forNewModelHelp).Select(t => t.Metadata.Name).Distinct();
        }

        private IEnumerable<string> GetTriggerTypesForHelp(string templateLanguage)
        {
            return GetLanguageTemplates(templateLanguage, forNewModelHelp: true).Select(t => t.Metadata.TriggerType).Distinct();
        }

        private IEnumerable<Template> GetLanguageTemplates(string templateLanguage, bool forNewModelHelp = false)
        {
            if (IsNewNodeJsProgrammingModel(workerRuntime) ||
                (forNewModelHelp && (Languages.TypeScript.EqualsIgnoreCase(templateLanguage) || Languages.JavaScript.EqualsIgnoreCase(templateLanguage))))
            {
                return _templates.Value.Where(t => t.Id.EndsWith("-4.x") && t.Metadata.Language.Equals(templateLanguage, StringComparison.OrdinalIgnoreCase));
            }

            return _templates.Value.Where(t => t.Metadata.Language.Equals(templateLanguage, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<string> GetTriggerNamesFromNewTemplates(string templateLanguage, bool forNewModelHelp = false)
        {
            return GetNewTemplates(templateLanguage, forNewModelHelp).Select(t => t.Name).Distinct();
        }

        private IEnumerable<NewTemplate> GetNewTemplates(string templateLanguage, bool forNewModelHelp = false)
        {
            if (IsNewPythonProgrammingModel() || (Languages.Python.EqualsIgnoreCase(templateLanguage) && forNewModelHelp))
            {
                return _newTemplates.Value.Where(t => t.Language.Equals(templateLanguage, StringComparison.OrdinalIgnoreCase));
            }

            throw new CliException("The new version of templates are only supported for Python.");
        }

        private void ConfigureAuthorizationLevel(Template template)
        {
            var bindings = template.Function["bindings"];
            bool IsHttpTriggerTemplate = bindings.Any(b => b["type"].ToString() == "httpTrigger");

            if (!IsHttpTriggerTemplate)
            {
                throw new CliException(AuthLevelErrorMessage);
            }
            else
            {
                var binding = bindings.Where(b => b["type"].ToString().Equals(HttpTriggerTemplateName, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                binding["authLevel"] = AuthorizationLevel.ToString();
            }
        }

        private bool InferAndUpdateLanguage(WorkerRuntime workerRuntime)
        {
            switch (workerRuntime)
            {
                case WorkerRuntime.dotnet:
                    // use fsproj as an indication that we have a F# project
                    Language = FileSystemHelpers.GetFiles(Environment.CurrentDirectory, searchPattern: "*.fsproj").Any() ? Constants.Languages.FSharp : Constants.Languages.CSharp;
                    return true;
                case WorkerRuntime.dotnetIsolated:
                    // use fsproj as an indication that we have a F# project
                    Language = FileSystemHelpers.GetFiles(Environment.CurrentDirectory, searchPattern: "*.fsproj").Any() ? Constants.Languages.FSharpIsolated : Constants.Languages.CSharpIsolated;
                    return true;
                case WorkerRuntime.node:
                    // use tsconfig.json as an indicator that we have a TypeScript project
                    Language = FileSystemHelpers.FileExists(Path.Combine(Environment.CurrentDirectory, "tsconfig.json")) ? Constants.Languages.TypeScript : Constants.Languages.JavaScript;
                    return true;
                case WorkerRuntime.None:
                case WorkerRuntime.python:
                case WorkerRuntime.java:
                case WorkerRuntime.powershell:
                case WorkerRuntime.custom:
                default:
                    return false;
            }
        }

        private void PerformPostDeployTasks(string functionName, string language)
        {
            if (language == Constants.Languages.TypeScript && !IsNewNodeJsProgrammingModel(workerRuntime))
            {
                // Update typescript function.json
                var funcJsonFile = Path.Combine(Environment.CurrentDirectory, functionName, Constants.FunctionJsonFileName);
                var jsonStr = FileSystemHelpers.ReadAllTextFromFile(funcJsonFile);
                var funcObj = JsonConvert.DeserializeObject<JObject>(jsonStr);
                funcObj.Add("scriptFile", $"../dist/{functionName}/index.js");
                FileSystemHelpers.WriteAllTextToFile(funcJsonFile, JsonConvert.SerializeObject(funcObj, Formatting.Indented));
            }
        }

        private void ParseTriggerForHelpRequest(string[] args)
        {
            if (args.Length != 2)
            {
                return;
            }

            var inputTriggerName = args[0];
            var inputHelp = args[1];
            if (HelpCommand.Equals(inputHelp, StringComparison.OrdinalIgnoreCase))
            {
                TriggerNameForHelp = inputTriggerName;
            }
        }

        public async Task<bool> ProcessHelpRequest(string triggerName, bool promptQuestions = false)
        {
            if (string.IsNullOrWhiteSpace(triggerName))
            {
                return false;
            }

            var supportedLanguages = new List<string>() { Languages.JavaScript, Languages.TypeScript, Languages.Python };
            if (string.IsNullOrEmpty(Language))
            {
                if (CurrentPathHasLocalSettings())
                {
                    await UpdateLanguageAndRuntime();
                }

                if (string.IsNullOrEmpty(Language) || !supportedLanguages.Contains(Language, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!promptQuestions)
                    {
                        return false;
                    }

                    SelectionMenuHelper.DisplaySelectionWizardPrompt("language");
                    Language = SelectionMenuHelper.DisplaySelectionWizard(supportedLanguages);
                }
            }

            IEnumerable<string> triggerNames;
            if (Languages.Python.EqualsIgnoreCase(Language))
            {
                triggerNames = GetTriggerNamesFromNewTemplates(Language, forNewModelHelp: true);
            }
            else
            {
                triggerNames = GetTriggerNames(Language, forNewModelHelp: true);
            }

            await _contextHelpManager.LoadTriggerHelp(Language, triggerNames.ToList());

            if (_contextHelpManager.IsValidTriggerNameForHelp(triggerName))
            {
                triggerName = _contextHelpManager.GetTriggerTypeFromTriggerNameForHelp(triggerName);
            }
            if (promptQuestions && !_contextHelpManager.IsValidTriggerTypeForHelp(triggerName))
            {
                ColoredConsole.WriteLine(ErrorColor($"The trigger name '{TriggerNameForHelp}' is not valid for {Language} language. "));
                SelectionMenuHelper.DisplaySelectionWizardPrompt("valid trigger");
                triggerName = SelectionMenuHelper.DisplaySelectionWizard(triggerNames);
                triggerName = _contextHelpManager.GetTriggerTypeFromTriggerNameForHelp(triggerName);
            }

            if (_contextHelpManager.IsValidTriggerTypeForHelp(triggerName))
            {
                ColoredConsole.Write(AdditionalInfoColor($"{Environment.NewLine}{_contextHelpManager.GetTriggerHelp(triggerName, Language)}"));
                return true;
            }

            return false;
        }

        private bool IsNewPythonProgrammingModel()
        {
            return PythonHelpers.IsNewPythonProgrammingModel(Language);
        }

        private bool IsNewNodeJsProgrammingModel(WorkerRuntime workerRuntime)
        {
            try
            {
                if (workerRuntime == WorkerRuntime.node)
                {
                    if (FileSystemHelpers.FileExists(Constants.PackageJsonFileName))
                    {
                        var packageJsonData = FileSystemHelpers.ReadAllTextFromFile(Constants.PackageJsonFileName);
                        var packageJson = JsonConvert.DeserializeObject<JToken>(packageJsonData);
                        var funcPackageVersion = packageJson["dependencies"]["@azure/functions"];
                        if (new Regex("^[^0-9]*4").IsMatch(funcPackageVersion.ToString()))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // ignore and assume "false"
            }

            return false;
        }

        private bool CurrentPathHasLocalSettings()
        {
            return FileSystemHelpers.FileExists(Path.Combine(Environment.CurrentDirectory, "local.settings.json"));
        }

        // New Template
        private void RunUserInputActions(IList<string> actionNames, IList<TemplateAction> actions, IDictionary<string, string> variables)
        {
            foreach (var actionName in actionNames)
            {
                var action = actions.First(x => actionName.Equals(x.Name, StringComparison.OrdinalIgnoreCase));
                if (action.ActionType != UserInputActionType)
                {
                    continue;
                }

                var userPrompt = _userPrompts.Value.First(x => x.Name == action.ParamId);
                var defaultValue = action.DefaultValue ?? userPrompt.DefaultValue;
                string response = string.Empty;
                if (userPrompt.Value == UserPromptEnumType || userPrompt.Value == UserPromptBooleanType)
                {
                    var values = new List<string>() { true.ToString(), false.ToString() };
                    if (userPrompt.Value == UserPromptEnumType)
                    {
                        values = userPrompt.EnumList.Select(x => x.Display).ToList();
                    }

                    while (!ValidateResponse(userPrompt, response))
                    {
                        SelectionMenuHelper.DisplaySelectionWizardPrompt(LabelMap(userPrompt.Label));
                        response = SelectionMenuHelper.DisplaySelectionWizard(values);

                        if (string.IsNullOrEmpty(response) && !string.IsNullOrEmpty(defaultValue))
                        {
                            response = defaultValue;
                        }
                        else if (userPrompt.Value == UserPromptEnumType)
                        {
                            response = userPrompt.EnumList.Single(x => x.Display == response).Value;
                        }
                    }
                }
                else
                {
                    // Use the function name if it is already provided by user
                    if (actionName.Equals(GetFunctionNameAction, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(FunctionName))
                    {
                        response = FunctionName;
                    }

                    // Use the route if it is already provided by user
                    if (actionName.Equals(GetHttpTriggerRouteAction, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Route))
                    {
                        response = Route;
                    }

                    while (!ValidateResponse(userPrompt, response))
                    {
                        PrintInputLabel(userPrompt, defaultValue);
                        response = Console.ReadLine();
                        if (string.IsNullOrEmpty(response) && !string.IsNullOrEmpty(defaultValue))
                        {
                            response = defaultValue;
                        }
                    }
                }

                var variableName = action.AssignTo;
                variables.Add(variableName, response);

                if (actionName.Equals(GetFunctionNameAction, StringComparison.OrdinalIgnoreCase))
                {
                    FunctionName = response;
                }
            }
        }

        private void PrintInputLabel(UserPrompt userPrompt, string defaultValue)
        {
            var label = LabelMap(userPrompt.Label);
            ColoredConsole.Write($"{label}: ");
            if (!string.IsNullOrEmpty(defaultValue))
            {
                ColoredConsole.Write($"[{defaultValue}] ");
            }
        }

        private bool ValidateResponse(UserPrompt userPrompt, string response)
        {
            if (string.IsNullOrEmpty(response))
            {
                return false;
            }

            var validator = userPrompt.Validators?.FirstOrDefault();
            if (validator == null)
            {
                return true;
            }

            var validationRegex = new Regex(validator.Expression);
            var isValid = validationRegex.IsMatch(response);

            if (!isValid)
            {
                ColoredConsole.WriteLine(ErrorColor($"{this.LabelMap(userPrompt.Label)} is not valid."));
            }

            return isValid;
        }

        private string LabelMap(string label)
        {
            if (!_newTemplateLabelMap.ContainsKey(label))
                return label;

            return _newTemplateLabelMap[label];
        }

        private static IDictionary<string, string> CreateLabelMap()
        {
            return new Dictionary<string, string>
            {
                { "$httpTrigger_route_label", "Route" },
                { "Provide a function name", "Function Name" },
                { "$httpTrigger_authLevel_label", "Auth Level" },
                { "$queueTrigger_queueName_label", "Queue Name" },
                { "$variables_storageConnStringLabel", "Storage Connection String" },
                { "cosmosDBTrigger-connectionStringSetting", "CosmosDB Connectiong Stirng" },
                { "$cosmosDBIn_databaseName_label", "CosmosDB Database Name" },
                { "$cosmosDBIn_collectionName_label", "CosmosDB Collection Name" },
                { "$cosmosDBIn_leaseCollectionName_label", "CosmosDB Lease Collection Name" },
                { "$cosmosDBIn_createIfNotExists_label", "Create If Not Exists" },
                { "$eventHubTrigger_connection_label", "EventHub Connection" },
                { "$eventHubOut_path_label", "EventHub Out Path" },
                { "$eventHubTrigger_consumerGroup_label", "EventHub Consumer Group" },
                { "$eventHubTrigger_cardinality_label", "EventHub Cardinality" },
                { "$serviceBusTrigger_connection_label", "Service Bus Connection" },
                { "$serviceBusTrigger_queueName_label", "Service Bus Queue Name" },
                { "$serviceBusTrigger_topicName_label", "Service Bus Topic Name" },
                { "$serviceBusTrigger_subscriptionName_label", "Service Bus Subscripton Name" },
                {"$timerTrigger_schedule_label", "Schedule" }
            };
        }


    }
}