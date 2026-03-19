using System.Reflection;
using Ev2.Backend.Common;

namespace DSPilot.TestConsole;

public static class ConnectionConfigExplorer
{
    public static void Explore()
    {
        Console.WriteLine("=== IConnectionConfiguration Exploration ===");
        Console.WriteLine();

        // ScanConfiguration의 Connection 프로퍼티에서 타입 가져오기
        var scanConfigType = typeof(ScanConfiguration);
        var connectionProp = scanConfigType.GetProperty("Connection");

        if (connectionProp != null)
        {
            var connectionType = connectionProp.PropertyType;
            Console.WriteLine($"Type: {connectionType.FullName}");
            Console.WriteLine($"Is interface: {connectionType.IsInterface}");
            Console.WriteLine();

            // 인터페이스의 멤버 조회
            var properties = connectionType.GetProperties();
            Console.WriteLine($"Properties ({properties.Length}):");
            foreach (var prop in properties)
            {
                Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");
            }

            Console.WriteLine();

            // 구현체 찾기 (다른 어셈블리에서)
            Console.WriteLine("Searching for implementations in loaded assemblies...");

            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in loadedAssemblies)
            {
                try
                {
                    var types = assembly.GetTypes()
                        .Where(t => connectionType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                        .ToList();

                    if (types.Any())
                    {
                        Console.WriteLine($"\nFound in {assembly.GetName().Name}:");
                        foreach (var implType in types)
                        {
                            Console.WriteLine($"  {implType.FullName}");

                            var ctors = implType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                            foreach (var ctor in ctors)
                            {
                                var parameters = ctor.GetParameters();
                                var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                                Console.WriteLine($"    new {implType.Name}({paramStr})");
                            }

                            // Static factory methods
                            var staticMethods = implType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .Where(m => m.ReturnType == implType || connectionType.IsAssignableFrom(m.ReturnType))
                                .ToList();

                            if (staticMethods.Any())
                            {
                                Console.WriteLine($"    Static factory methods:");
                                foreach (var method in staticMethods)
                                {
                                    var parameters = method.GetParameters();
                                    var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                                    Console.WriteLine($"      static {method.ReturnType.Name} {method.Name}({paramStr})");
                                }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // 스킵
                }
                catch (Exception)
                {
                    // 스킵
                }
            }
        }

        Console.WriteLine();
    }
}
