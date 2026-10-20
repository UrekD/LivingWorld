// .NET 10 adds System.Reflection.MethodInvoker, which makes Spaceports' unqualified MethodInvoker ambiguous.
global using MethodInvoker = HarmonyLib.MethodInvoker;
