using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace InGameTests.Tests
{
    /// <summary>
    /// Checks of every enabled Harmony mod in scope (see ModScope) that need no game time. They
    /// catch what a passive run can't: a patch that never applied, and a transpiler whose IL
    /// search silently failed.
    /// </summary>
    internal static class HarmonyModTests
    {
        private static readonly string[] PatchKinds = { "Prefix", "Postfix", "Transpiler", "Finalizer", "ILManipulator" };

        /// <summary>
        /// Every patch method a mod declares is applied under its HarmonyID. Harmony skips the
        /// patch classes in types that fail to load, and the mod loader only logs that.
        /// </summary>
        [InGameTest(Order = 0)]
        private static IEnumerator EveryPatchMethodIsApplied(TestContext ctx)
        {
            var applied = new HashSet<string>();
            foreach (MethodBase original in Harmony.GetAllPatchedMethods())
            {
                foreach (Patch patch in AllPatches(Harmony.GetPatchInfo(original)))
                {
                    applied.Add(patch.owner + " " + Key(patch.PatchMethod));
                }
            }

            int expected = 0;
            List<HarmonyMod> mods = HarmonyMod.Enabled();
            ModScope.NoteSkipped(ctx, mods.Where(m => !ModScope.Includes(m.Mod)).Select(m => m.Title));
            // A mod whose assembly isn't loaded fails EveryEnabledHarmonyModIsPatched.
            foreach (HarmonyMod mod in mods.Where(m => m.Assembly != null && ModScope.Includes(m.Mod)))
            {
                Type[] types;
                try
                {
                    types = mod.Assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct())
                    {
                        ctx.Fail(mod.Title + ": some types failed to load, so their patches were skipped: " + message);
                    }
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                {
                    // PatchAll only processes classes with a class-level Harmony attribute.
                    bool classAnnotated = type.IsDefined(typeof(HarmonyAttribute), false);
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (PatchKind(method) == null || method.IsDefined(typeof(HarmonyReversePatch), false))
                        {
                            continue;
                        }
                        if (!classAnnotated && !method.IsDefined(typeof(HarmonyPatch), false))
                        {
                            continue;
                        }
                        expected++;
                        if (!applied.Contains(mod.HarmonyId + " " + Key(method)))
                        {
                            ctx.Fail(mod.Title + ": " + type.FullName + "." + method.Name + " is not applied"
                                + (classAnnotated ? "" : " (its class has no [HarmonyPatch], so PatchAll skips it)"));
                        }
                    }
                }
            }
            ctx.Record("patchMethodsChecked", expected);
            yield break;
        }

        /// <summary>
        /// Each mod transpiler changes its method's IL. A transpiler whose IL search finds nothing
        /// usually returns the instructions untouched, and the mod then does nothing without an error.
        /// Re-runs the patched methods' transpilers one at a time and compares the IL before and after each.
        /// </summary>
        [InGameTest(Order = 0)]
        private static IEnumerator EveryTranspilerChangesIL(TestContext ctx)
        {
            List<HarmonyMod> mods = HarmonyMod.Enabled();
            ModScope.NoteSkipped(ctx, mods.Where(m => !ModScope.Includes(m.Mod)).Select(m => m.Title));
            Dictionary<string, string> titles = mods.Where(m => ModScope.Includes(m.Mod)).ToDictionary(m => m.HarmonyId, m => m.Title);
            int checkedTranspilers = 0;
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (!info.Transpilers.Any(p => titles.ContainsKey(p.owner)))
                {
                    continue;
                }

                string target = original.DeclaringType.FullName + "." + original.Name;
                try
                {
                    // Sorted the way Harmony applies them; GetCurrentInstructions applies the first N.
                    List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(original, info.Transpilers.ToArray());
                    string before = Dump(PatchProcessor.GetCurrentInstructions(original, 0));
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        string after = Dump(PatchProcessor.GetCurrentInstructions(original, i + 1));
                        Patch patch = info.Transpilers.First(p => Key(p.PatchMethod) == Key(sorted[i]));
                        if (titles.TryGetValue(patch.owner, out string title))
                        {
                            checkedTranspilers++;
                            if (after == before)
                            {
                                ctx.Fail(title + ": " + sorted[i].DeclaringType.FullName + "." + sorted[i].Name
                                    + " leaves " + target + " unchanged; its IL search probably found nothing");
                            }
                        }
                        before = after;
                    }
                }
                catch (Exception ex)
                {
                    ctx.Fail("Re-running the transpilers of " + target + " threw: " + ex);
                }
            }
            ctx.Record("transpilersChecked", checkedTranspilers);
            yield break;
        }

        private static IEnumerable<Patch> AllPatches(Patches info)
        {
            return info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);
        }

        /// <summary>The patch type a method declares by attribute or by name, or null.</summary>
        private static string PatchKind(MethodInfo method)
        {
            foreach (object attribute in method.GetCustomAttributes(false))
            {
                string name = attribute.GetType().Name;
                if (name.StartsWith("Harmony", StringComparison.Ordinal) && PatchKinds.Contains(name.Substring("Harmony".Length)))
                {
                    return name.Substring("Harmony".Length);
                }
            }
            return PatchKinds.Contains(method.Name) ? method.Name : null;
        }

        private static string Key(MethodBase method) => method.Module.ModuleVersionId + ":" + method.MetadataToken;

        /// <summary>Instructions as text. Label and local numbers are stable between calls for the same transpilers.</summary>
        private static string Dump(List<CodeInstruction> code)
        {
            var sb = new StringBuilder();
            foreach (CodeInstruction ci in code)
            {
                sb.Append(ci.opcode.Name).Append(' ').Append(Operand(ci.operand));
                foreach (Label label in ci.labels)
                {
                    sb.Append(" L").Append(label.GetHashCode());
                }
                foreach (ExceptionBlock block in ci.blocks)
                {
                    sb.Append(" B").Append(block.blockType).Append(block.catchType);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Operand(object operand)
        {
            switch (operand)
            {
                case null:
                    return "";
                case Label label:
                    return "L" + label.GetHashCode();
                case Label[] labels:
                    return string.Join(",", labels.Select(l => "L" + l.GetHashCode()).ToArray());
                case LocalBuilder local:
                    return "V" + local.LocalIndex + ":" + local.LocalType;
                case MemberInfo member:
                    return member.DeclaringType + "::" + member;
                default:
                    return Convert.ToString(operand, CultureInfo.InvariantCulture);
            }
        }
    }
}
