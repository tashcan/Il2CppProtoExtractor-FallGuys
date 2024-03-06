using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppInspector.Reflection;

namespace FallGuysProtoDumper
{
    class Program
    {
        // Set the path to your metadata and binary files here
        public static string MetadataFile = @"C:\Games\Star Trek Fleet Command\default\game\prime_Data\il2cpp_data\Metadata\global-metadata.dat";
        public static string BinaryFile = @"C:\Games\Star Trek Fleet Command\default\game\GameAssembly.dll";

        // Set the path to your desired output here
        public static string ProtoFile = @"stfc.proto";

        // Define type map from .NET types to protobuf types
        // This is specifically how protobuf-net maps types and is not the same for all .NET protobuf libraries
        private static Dictionary<string, string> protoTypes = new Dictionary<string, string> {
            ["System.Int32"] = "int32",
            ["System.UInt32"] = "uint32",
            ["System.Byte"] = "uint32",
            ["System.SByte"] = "int32",
            ["System.UInt16"] = "uint32",
            ["System.Int16"] = "int32",
            ["System.Int64"] = "int64",
            ["System.UInt64"] = "uint64",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Decimal"] = "bcl.Decimal",
            ["System.Boolean"] = "bool",
            ["System.String"] = "string",
            ["System.Byte[]"] = "bytes",
            ["System.Char"] = "uint32",
            ["System.DateTime"] = "google.protobuf.Timestamp",
            ["Google.Protobuf.WellKnownTypes.Timestamp"] = "google.protobuf.Timestamp",
            ["Google.Protobuf.ByteString"] = "bytes",
        };

        static void Main(string[] args) {

            // First we load the binary and metadata files into Il2CppInspector
            // There is only one image so we use [0] to select this image
            Console.WriteLine("Loading package...");
            var package = Il2CppInspector.Il2CppInspector.LoadFromFile(BinaryFile, MetadataFile, silent: false)[0];

            // Now we create the .NET type model from the package
            // This creates a .NET Reflection-style interface we can query with Linq
            Console.WriteLine("Creating type model...");
            var model = new TypeModel(package);

            // All protobuf messages have this class attribute
            var protoContract = model.GetType("Google.Protobuf.IMessage");
            var dataEnum = model.GetType("Digit.Utilities.IDataEnum");

            // Get all the messages by searching for types with [ProtoContract]
            var messages = model.TypesByDefinitionIndex.Where(t => t.ImplementedInterfaces.Any(a => a == protoContract) && t.DeclaringType == null);

            messages = messages.Where(m => m.Namespace.StartsWith("Google.Protobuf") == false);

            var dataEnums = model.TypesByDefinitionIndex.Where(t => t.ImplementedInterfaces.Any(a => a == dataEnum));

            Directory.CreateDirectory("output");

            List<string> packages = new List<string>();

            Dictionary<TypeInfo, string> messageToPackage = new Dictionary<TypeInfo, string>();

            // Collect all messages and their corressponding package
            foreach (var messageGroup in messages.GroupBy(m => m.Namespace).OrderByDescending(g => g.Key.Length))
            {
                var namespaceName = messageGroup.Key;
                if (namespaceName.Length > 0)
                {
                    foreach (var message in messageGroup)
                    {
                        messageToPackage[message] = namespaceName;
                    }
                }
            }

            foreach (var messageGroup in dataEnums.GroupBy(m => m.Namespace).OrderByDescending(g => g.Key.Length))
            {
                var namespaceName = messageGroup.Key;
                if (namespaceName.Length > 0)
                {
                    foreach (var message in messageGroup)
                    {
                        messageToPackage[message] = namespaceName;
                    }
                }
            }


            foreach (var messageGroup in messages.GroupBy(m => m.Namespace).OrderByDescending(g => g.Key.Length))
            {
                // Keep a list of all the enums we need to output (HashSet ensures unique values - we only want each enum once!)
                var enums = new HashSet<TypeInfo>();

                // Output proto file text
                StringBuilder proto = new StringBuilder();

                // Output messages
                var banner = @"
syntax=""proto3"";

option optimize_for = LITE_RUNTIME;

import ""google/protobuf/timestamp.proto"";

";

                StringBuilder packageString = new StringBuilder();

                var namespaceName = messageGroup.Key;
                if (namespaceName.Length == 0)
                {
                    namespaceName = "stfc";
                } 
                else
                {
                    packages.Add(namespaceName);
                    packageString.Append("package " + namespaceName + ";\n\n");
                }

                var requiredImports = new HashSet<string>();

                foreach (var message in messageGroup) {
                    PrintMessage(model, protoContract, enums, message, proto, messageToPackage, requiredImports);
                }

                // Output enums
                var enumText = new StringBuilder();

                foreach(var e in dataEnums.Where(e => e.Namespace == messageGroup.Key))
                {
                    PrintDataEnum(enumText, e);
                }

                foreach (var e in enums)
                {
                    PrintEnum(enumText, e);
                }

                foreach(var import in requiredImports)
                {
                    if (import != namespaceName)
                        banner += "import \"" + import + ".proto\";\n";
                }

                File.WriteAllText("output/" + namespaceName + ".proto", banner + packageString.ToString() + enumText.ToString() + proto.ToString());
            }

        }

        private static void PrintMessage(TypeModel model, TypeInfo protoContract, HashSet<TypeInfo> enums, TypeInfo message, StringBuilder proto, Dictionary<TypeInfo, string> messageToPackage, HashSet<String> requiredImports)
        {
            var props = message.DeclaredProperties.Where(p => message.DeclaredFields.Any(f => f.Name == p.Name + "FieldNumber")).Select(p =>
            {
                return (p, (int)message.DeclaredFields.First(f => f.Name == p.Name + "FieldNumber").DefaultValue);
            });

            var oneOf = message.DeclaredProperties.Where(p => p.PropertyType.CSharpName.EndsWith("OneofCase"));
            props = props.Where(p =>
            {
                var prop = p.Item1;
                var name = prop.Name;
                return !oneOf.Any(o => o.PropertyType.GetEnumNames().Any(n => n == name));
            });
            
            var name = message.CSharpName;
            proto.Append($"message {name} {{\n");

            var nestedTypes = message.DeclaredNestedTypes.Where(t => t.CSharpName == "Types").SelectMany(t => t.DeclaredNestedTypes);
            nestedTypes = nestedTypes.Where(t => t.ImplementedInterfaces.Any(a => a == protoContract));

            foreach (var nested in nestedTypes)
            {
                PrintMessage(model, protoContract, enums, nested, proto, messageToPackage, requiredImports);
            }

            var GetType = (object f) =>
            {
                if (f.GetType() == typeof(FieldInfo))
                {
                    return ((FieldInfo)f).FieldType;
                }
                else if (f.GetType() == typeof(PropertyInfo))
                {
                    return ((PropertyInfo)f).PropertyType;
                }
                return null;
            };

            foreach (var p in oneOf)
            {
                proto.Append($"oneof {p.Name[..^"Case".Length]} {{\n");
                foreach (var propName in p.PropertyType.GetEnumNames())
                {
                    if (propName == "None")
                        continue;

                    var field = message.DeclaredFields.FirstOrDefault(f => f.Name == propName + "FieldNumber");
                    if (field == null)
                        continue;

                    var prop = message.DeclaredProperties.Where(p => p.Name == propName);
                    var propType = GetType(prop.First());
                    var fieldNumber = (int)field.DefaultValue;
                    outputField(model, char.ToLower(propName[0]) + propName.Substring(1), message, propType, fieldNumber, proto, messageToPackage, requiredImports, enums);
                }
                proto.Append("}\n\n");
            }

            //// Output C# properties
            foreach (var (prop, _) in props)
            {
                var propType = GetType(prop);


                if (propType.IsEnum)
                {
                    if (propType.DeclaringType != null)
                    {
                        PrintEnum(proto, propType);
                    }
                    else
                    {
                        enums.Add(propType);
                    }
                }
            }

            foreach (var (prop, fieldNumber) in props)
            {
                var propType = GetType(prop);
                // MEGA HACK
                propType = ResolveInterfaceType(model, propType);
                var propName = prop.Name;
                outputField(model, char.ToLower(propName[0]) + propName.Substring(1), message, propType, fieldNumber, proto, messageToPackage, requiredImports, enums);
            }

            proto.Append("}\n\n");
        }

        private static void PrintEnum(StringBuilder proto, TypeInfo prop)
        {
            proto.Append("enum " + prop.CSharpName + " {\n");
            var namesAndValues = prop.GetEnumNames().Zip(prop.GetEnumValues().Cast<int>(), (n, v) =>
            {
                return (prop.CSharpName.ToUpper() + "_" + n.ToUpper(), v);
            });

            if (!namesAndValues.Any(kv => kv.Item2 == 0))
            {
                proto.Append("  " + prop.CSharpName.ToUpper() + "_NONE = 0" + ";\n");
            }
            else if (namesAndValues.First().Item2 != 0)
            {
                namesAndValues = namesAndValues.OrderBy(kv => (uint)kv.Item2);
            }
            foreach (var nv in namesAndValues)
                proto.Append("  " + nv.Item1 + " = " + nv.Item2 + ";\n");
            proto.Append("}\n\n");
        }

        private static void PrintDataEnum(StringBuilder proto, TypeInfo prop)
        {
            proto.Append("enum " + prop.CSharpName + " {\n");
            var dataEnumValues = prop.DeclaredNestedTypes.Where(e => e.CSharpName == "Values").First();
            var namesAndValues = dataEnumValues.DeclaredFields.Select(f =>
            {
                return (prop.CSharpName.ToUpper() + "_" + f.Name.ToUpper(), (int)f.DefaultValue);
            });

            if (!namesAndValues.Any(kv => kv.Item2 == 0))
            {
                proto.Append("  " + prop.CSharpName.ToUpper() + "_NONE = 0" + ";\n");
            }
            else if (namesAndValues.First().Item2 != 0)
            {
                namesAndValues = namesAndValues.OrderBy(kv => (uint)kv.Item2);
            }
            foreach (var nv in namesAndValues)
                proto.Append("  " + nv.Item1 + " = " + nv.Item2 + ";\n");
            proto.Append("}\n\n");
        }

        private static string getParentNameFull(TypeInfo type)
        {
            var declaringType = type;
            var result = string.Empty;
            while (declaringType != null)
            {
                if (declaringType.CSharpName != "Types")
                {
                    result += (result == string.Empty ? "" : ".") + declaringType.CSharpName;
                }
                declaringType = declaringType.DeclaringType;
            }
            return result;
        }

        private static string getCleanFullName(TypeInfo type, TypeInfo fieldDeclaringType)
        {
            if (type.DeclaringType != null && type.Namespace.StartsWith("System.") == false)
            {
                return type.CSharpName;
            }
            else
            {
                return type.FullName;
            }
        }

        private static string getCleanName(TypeInfo type, TypeInfo fieldDeclaringType)
        {
            if (type.DeclaringType != null && type.Namespace.StartsWith("System.") == false)
            {
                if ((type.DeclaringType.CSharpName != "Types" && type.DeclaringType != fieldDeclaringType) 
                    || (type.DeclaringType.CSharpName == "Types" && type.DeclaringType.DeclaringType != fieldDeclaringType))
                {
                    return getParentNameFull(type.DeclaringType) + "." + type.CSharpName;
                }
                return type.CSharpName;
            }
            else
            {
                return type.Name;
            }
        }

        // Output a single field definition in a protobuf message
        private static void outputField(TypeModel model, string name, TypeInfo message, TypeInfo type, int pmAtt, StringBuilder proto, Dictionary<TypeInfo, String> messageToPackage, HashSet<String> requiredImports, HashSet<TypeInfo> enums)
        {
            // Handle arrays
            var isRepeated = type.IsArray;
            var isNested = type.DeclaringType != null;
            var isOptional = false;

            var realType = isRepeated ? type.ElementType : type;
            realType = ResolveInterfaceType(model, realType);

            var typeFullName = getCleanFullName(realType, message) ?? string.Empty;
            var typeFriendlyName = getCleanName(realType, message);

            // Handle one-dimensional collections like lists
            // We could also use type.Namespace == "System.Collections.Generic" && type.UnmangledBaseName == "List"
            // or typeBaseName == "System.Collections.Generic.List`1" but these are less flexible
            if (type.ImplementedInterfaces.Any(i => i.FullName == "System.Collections.Generic.IList`1"))
            {
                // Get the type of the IList by looking at its first generic argument
                // Note this is a naive implementation which doesn't handle nesting of lists or arrays in lists etc.

                if (type.GenericTypeArguments[0].IsEnum)
                    enums.Add(ResolveInterfaceType(model, type.GenericTypeArguments[0]));
                typeFullName = getCleanFullName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message);
                typeFriendlyName = getCleanName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message);
                isRepeated = true;
            }

            // Handle maps (IDictionary)
            if (type.ImplementedInterfaces.Any(i => i.FullName == "System.Collections.Generic.IDictionary`2"))
            {

                // This time we have two generic arguments to deal with - the key and the value

                if (type.GenericTypeArguments[0].IsEnum)
                    enums.Add(ResolveInterfaceType(model, type.GenericTypeArguments[0]));

                if (type.GenericTypeArguments[1].IsEnum)
                    enums.Add(type.GenericTypeArguments[1]);

                var keyFullName = getCleanFullName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message);
                var valueFullName = getCleanFullName(ResolveInterfaceType(model, type.GenericTypeArguments[1]), message);

                // We're going to have to deal with building this proto type name separately from the value types below
                // We don't set isRepeated because it's implied by using a map type
                protoTypes.TryGetValue(keyFullName, out var keyFriendlyName);
                protoTypes.TryGetValue(valueFullName, out var valueFriendlyName);
                typeFriendlyName = $"map<{keyFriendlyName ?? getCleanName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message)}, {valueFriendlyName ?? getCleanName(ResolveInterfaceType(model, type.GenericTypeArguments[1]), message)}>";
            }

            // Handle nullable types
            if (type.FullName == "System.Nullable`1")
            {
                // Once again we look at the first generic argument to get the real type
                if (type.GenericTypeArguments[0].IsEnum)
                    enums.Add(type.GenericTypeArguments[0]);

                typeFullName = getCleanFullName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message);
                typeFriendlyName = getCleanName(ResolveInterfaceType(model, type.GenericTypeArguments[0]), message);
                isOptional = true;
            }

            // Handle primitive value types
            if (protoTypes.TryGetValue(typeFullName, out var protoTypeName))
                typeFriendlyName = protoTypeName;

            // Handle repeated fields
            var annotatedName = typeFriendlyName;

            var mappingType = isRepeated ? type.GenericTypeArguments[0] : type;
            if (messageToPackage.ContainsKey(mappingType))
            {
                if ((isRepeated ? type.GenericTypeArguments[0] : type).Namespace != message.Namespace)
                {
                    annotatedName = messageToPackage[mappingType] + "." + annotatedName;
                    requiredImports.Add(messageToPackage[mappingType]);
                }
            }

            if (isRepeated)
                annotatedName = "repeated " + annotatedName;

            // Handle nullable (optional) fields
            if (isOptional)
                annotatedName = "optional " + annotatedName;

            // Output field
            proto.Append($"  {annotatedName} {name} = {pmAtt};\n");
        }

        private static TypeInfo ResolveInterfaceType(TypeModel model, TypeInfo realType)
        {
            if (realType.IsAbstract || realType.IsInterface)
            {
                // Resolve interface
                var typesImplementingInterface = model.Types.Where(typeinfo =>
                {
                    return typeinfo.ImplementedInterfaces.Contains(realType);
                });
                if (realType.CSharpName.StartsWith("I"))
                {
                    var propNameNoInterface = realType.CSharpName.Substring(1);
                    realType = typesImplementingInterface.Where(t => t.CSharpName == propNameNoInterface).First();
                }
            }

            return realType;
        }
    }
}
