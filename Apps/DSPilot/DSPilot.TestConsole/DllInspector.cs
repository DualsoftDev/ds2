using System.Reflection;

namespace DSPilot.TestConsole;

public static class DllInspector
{
    private static string GetFriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var genericTypeName = type.GetGenericTypeDefinition().Name;
        var genericArgs = type.GetGenericArguments();
        var genericArgNames = string.Join(", ", genericArgs.Select(GetFriendlyTypeName));
        return $"{genericTypeName.Substring(0, genericTypeName.IndexOf('`'))}<{genericArgNames}>";
    }

    public static void InspectDll(string dllPath)
    {
        Console.WriteLine($"=== Inspecting DLL: {dllPath} ===");
        Console.WriteLine();

        try
        {
            var assembly = Assembly.LoadFrom(dllPath);

            Console.WriteLine($"Assembly: {assembly.GetName().Name}");
            Console.WriteLine($"Version: {assembly.GetName().Version}");
            Console.WriteLine();

            var types = assembly.GetTypes()
                .Where(t => t.IsPublic)
                .OrderBy(t => t.Namespace)
                .ThenBy(t => t.Name)
                .ToList();

            Console.WriteLine($"Total public types: {types.Count}");
            Console.WriteLine();

            // 모든 타입 출력
            Console.WriteLine("=== All Public Types ===");
            foreach (var type in types.Take(50))
            {
                Console.WriteLine($"  {type.FullName}");
            }
            Console.WriteLine();

            // Detailed inspection of key types
            var keyTypeNames = new[] { "PLCBackendService", "TagHistoricWAL", "IWalBuffer", "TagLogEntry", "ScanConfiguration", "PlcValue", "WAL" };

            foreach (var typeName in keyTypeNames)
            {
                var matchingTypes = types.Where(t => t.Name == typeName).ToList();

                if (matchingTypes.Any())
                {
                    Console.WriteLine($"=== Detailed Analysis: {typeName} ===");
                    foreach (var type in matchingTypes)
                    {
                        Console.WriteLine($"Full name: {type.FullName}");
                        Console.WriteLine($"Namespace: {type.Namespace}");
                        Console.WriteLine($"Is class: {type.IsClass}");
                        Console.WriteLine($"Is interface: {type.IsInterface}");
                        Console.WriteLine();

                        // Constructors
                        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                        if (ctors.Length > 0)
                        {
                            Console.WriteLine("Constructors:");
                            foreach (var ctor in ctors)
                            {
                                var parameters = ctor.GetParameters();
                                var paramStr = string.Join(", ", parameters.Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"));
                                Console.WriteLine($"  new {type.Name}({paramStr})");
                            }
                            Console.WriteLine();
                        }

                        // Static methods
                        var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(m => !m.IsSpecialName)
                            .ToList();

                        if (staticMethods.Any())
                        {
                            Console.WriteLine("Static methods:");
                            foreach (var method in staticMethods)
                            {
                                var parameters = method.GetParameters();
                                var paramStr = string.Join(", ", parameters.Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"));
                                Console.WriteLine($"  static {GetFriendlyTypeName(method.ReturnType)} {method.Name}({paramStr})");
                            }
                            Console.WriteLine();
                        }

                        // Instance methods
                        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Where(m => !m.IsSpecialName)
                            .ToList();

                        if (methods.Any())
                        {
                            Console.WriteLine("Instance methods:");
                            foreach (var method in methods)
                            {
                                var parameters = method.GetParameters();
                                var paramStr = string.Join(", ", parameters.Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"));
                                Console.WriteLine($"  {GetFriendlyTypeName(method.ReturnType)} {method.Name}({paramStr})");
                            }
                            Console.WriteLine();
                        }

                        // Properties
                        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                        if (props.Length > 0)
                        {
                            Console.WriteLine("Properties:");
                            foreach (var prop in props)
                            {
                                Console.WriteLine($"  {GetFriendlyTypeName(prop.PropertyType)} {prop.Name} {{ {(prop.CanRead ? "get; " : "")}{(prop.CanWrite ? "set; " : "")}}}");
                            }
                            Console.WriteLine();
                        }

                        // Events
                        var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance);
                        if (events.Length > 0)
                        {
                            Console.WriteLine("Events:");
                            foreach (var evt in events)
                            {
                                Console.WriteLine($"  event {GetFriendlyTypeName(evt.EventHandlerType!)} {evt.Name}");
                            }
                            Console.WriteLine();
                        }

                        Console.WriteLine();
                    }
                }
            }

            // Broader search for patterns
            var targetTypes = new[] { "Subject", "Observable", "Plc", "Entity", "Tag", "Communication", "Scan", "Config", "Value" };

            foreach (var typeName in targetTypes)
            {
                var matchingTypes = types.Where(t => t.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matchingTypes.Any())
                {
                    Console.WriteLine($"--- Found type matching '{typeName}' ---");
                    foreach (var type in matchingTypes.Take(3))
                    {
                        Console.WriteLine($"  {type.FullName}");
                    }
                    if (matchingTypes.Count > 3)
                    {
                        Console.WriteLine($"  ... and {matchingTypes.Count - 3} more");
                    }
                    Console.WriteLine();
                }
            }

            // List all namespaces
            var namespaces = types.Select(t => t.Namespace).Distinct().OrderBy(ns => ns).ToList();
            Console.WriteLine("=== All namespaces in DLL ===");
            foreach (var ns in namespaces)
            {
                var nsTypes = types.Where(t => t.Namespace == ns).ToList();
                Console.WriteLine($"{ns} ({nsTypes.Count} types)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inspecting DLL: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}
