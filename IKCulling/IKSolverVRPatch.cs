using FrooxEngine;
using FrooxEngine.FinalIK;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace IKCulling
{
    [HarmonyPatch]
    internal class IKSolverVRPatch
    {
        private static readonly MethodInfo deltaTimeInjector = AccessTools.Method(typeof(IKCulling), nameof(IKCulling.getProgressiveDeltaTime));
        private static readonly MethodInfo workerTimeGetter = AccessTools.PropertyGetter(typeof(Worker), "Time");

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = AccessTools.GetTypesFromAssembly(AccessTools.AllAssemblies().First(assembly => assembly.GetName().Name == "FrooxEngine"))
                .Where(type => !type.IsAbstract && type.FullName.Contains("IK"))
                .SelectMany(type => type.GetMethods(AccessTools.all).Where(method => method.Name == "Solve"));

            IKCulling.Msg("Found methods to patch:");

            foreach (var method in methods)
                IKCulling.Msg(method.FullDescription());

            return methods;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var skipNext = false;

            foreach (var instruction in instructions)
            {
                if (skipNext)
                {
                    skipNext = false;
                    continue;
                }

                if (instruction.Calls(workerTimeGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, deltaTimeInjector);
                    skipNext = true;
                }
                else
                    yield return instruction;
            }
        }
    }
}