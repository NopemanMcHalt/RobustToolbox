using System;
using System.Linq;
using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.IoC;

namespace Robust.Client.Console.Commands
{
    [UsedImplicitly]
    internal sealed class CVarCommand : SharedCVarCommand, IConsoleCommand
    {
        public override void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length < 1 || args.Length > 2)
            {
                shell.WriteError("Must provide exactly one or two arguments.");
                return;
            }

            var configManager = IoCManager.Resolve<IConfigurationManager>();
            var name = args[0];

            if (name == "?")
            {
                var cvars = configManager.GetRegisteredCVars().OrderBy(c => c);
                shell.WriteLine(string.Join("\n", cvars));
                return;
            }

            if (!configManager.IsCVarRegistered(name))
            {
                shell.WriteError($"CVar '{name}' is not registered. Use 'cvar ?' to get a list of all registered CVars.");
                return;
            }

            if (args.Length == 1)
            {
                // Read CVar
                var value = configManager.GetCVar<object>(name);
                shell.WriteLine(value.ToString() ?? "");
            }
            else
            {
                // Write CVar
                var value = args[1];
                var type = configManager.GetCVarType(name);
                try
                {
                    var parsed = ParseObject(type, value);
                    configManager.SetCVar(name, parsed);
                }
                catch (FormatException)
                {
                    shell.WriteLine($"Input value is in incorrect format for type {type}");
                }
            }
        }
    }

    [UsedImplicitly]
    public sealed class SaveConfig : IConsoleCommand
    {
        public string Command => "saveconfig";
        public string Description => "Saves the client configuration to the config file";
        public string Help => "saveconfig";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            IoCManager.Resolve<IConfigurationManager>().SaveToFile();
        }
    }

}
