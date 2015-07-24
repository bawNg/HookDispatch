using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Mono.Cecil.Cil;
using Mono.Cecil;
using System.IO;
using System.Reflection;
using Mono.Cecil.Rocks;
using Oxide.Plugins;

namespace HookDispatch
{
    class Program
    {
        public class DirectCallMethod
        {
            public class Node
            {
                public char Char;
                public string Name;
                public Dictionary<char, Node> Edges = new Dictionary<char, Node>();
                public Node Parent;
            }

            private ModuleDefinition module;
            private TypeDefinition type;
            private MethodDefinition method;
            private Mono.Cecil.Cil.MethodBody body;
            private Instruction endInstruction;

            private List<Instruction> firstNodeInstructions = new List<Instruction>();
            private Dictionary<int, Instruction> jumpToNextEdgePlaceholders = new Dictionary<int, Instruction>();
            private List<Instruction> jumpToEndPlaceholders = new List<Instruction>();

            private Dictionary<string, MethodDefinition> hookMethods = new Dictionary<string, MethodDefinition>();

            private MethodReference getLength;
            private MethodReference getChars;

            public DirectCallMethod(ModuleDefinition module, TypeDefinition type)
            {
                this.module = module;
                this.type = type;

                getLength = module.Import(typeof(string).GetMethod("get_Length", new Type[0]));
                getChars = module.Import(typeof(string).GetMethod("get_Chars", new[] { typeof(int) }));
                
                // Copy method definition from base class
                var base_assembly = AssemblyDefinition.ReadAssembly("HookDispatch.exe");
                var base_module = base_assembly.MainModule;
                var base_type = base_assembly.MainModule.GetType("Oxide.Plugins.Plugin");
                var base_method = base_type.Methods.First(method => method.Name == "DirectCallHook");

                // Create method override based on virtual method signature
                method = new MethodDefinition(base_method.Name, base_method.Attributes, base_module.Import(base_method.ReturnType));
                foreach (var parameter in base_method.Parameters)
                    method.Parameters.Add(new ParameterDefinition(base_module.Import(parameter.ParameterType)));

                method.ImplAttributes = base_method.ImplAttributes;
                method.SemanticsAttributes = base_method.SemanticsAttributes;

                // Replace the NewSlot attribute with ReuseSlot
                method.Attributes &= ~Mono.Cecil.MethodAttributes.NewSlot;
                method.Attributes |= Mono.Cecil.MethodAttributes.ReuseSlot;

                // Create new method body
                body = new Mono.Cecil.Cil.MethodBody(method);
                body.SimplifyMacros();
                method.Body = body;
                type.Methods.Add(method);

                // Create variables
                body.Variables.Add(new VariableDefinition("name_size", module.TypeSystem.Int32));
                body.Variables.Add(new VariableDefinition("i", module.TypeSystem.Int32));

                // Initialize return value to null
                AddInstruction(OpCodes.Ldarg_2);
                AddInstruction(OpCodes.Ldnull);
                AddInstruction(OpCodes.Stind_Ref);

                // Get method name length
                AddInstruction(OpCodes.Ldarg_1);
                AddInstruction(OpCodes.Callvirt, getLength);
                AddInstruction(OpCodes.Stloc_0);

                // Initialize i counter variable to 0
                AddInstruction(OpCodes.Ldc_I4_0);
                AddInstruction(OpCodes.Stloc_1);

                // Find all hook methods defined by the plugin
                hookMethods = type.Methods.Where(m => !m.IsStatic && m.IsPrivate).ToDictionary(m => m.Name, m => m);

                // Build a hook method name trie
                var root_node = new Node();
                foreach (var method_name in hookMethods.Keys)
                {
                    var current_node = root_node;
                    for (var i = 1; i <= method_name.Length; i++)
                    {
                        var letter = method_name[i - 1];
                        Node next_node;
                        if (!current_node.Edges.TryGetValue(letter, out next_node))
                        {
                            next_node = new Node { Parent = current_node, Char = letter };
                            if (i == method_name.Length) next_node.Name = method_name;
                            current_node.Edges[letter] = next_node;
                        }
                        current_node = next_node;
                    }
                }

                // Build conditional method call logic from trie nodes
                var n = 1;
                foreach (var edge in root_node.Edges.Keys)
                    BuildNode(root_node.Edges[edge], n++);

                // No valid method was found
                endInstruction = Return(false);
                
                foreach (var i in jumpToNextEdgePlaceholders.Keys)
                {
                    var instruction = jumpToNextEdgePlaceholders[i];
                    instruction.Operand = i < firstNodeInstructions.Count ? firstNodeInstructions[i] : endInstruction;
                }

                foreach (var instruction in jumpToEndPlaceholders)
                {
                    instruction.Operand = endInstruction;
                }

                //UpdateInstructions();

                body.OptimizeMacros();

                foreach (var i in jumpToNextEdgePlaceholders.Keys)
                {
                    Puts($"Jump {i}: {jumpToNextEdgePlaceholders[i]}");
                }

                foreach (var instruction in jumpToEndPlaceholders)
                {
                    Puts($"Jump to end: {instruction}");
                }

                foreach (var instruction in body.Instructions)
                {
                    var line = instruction.ToString();
                    Puts(line);
                    if (line.Contains(" bne.un.s") || line.Contains(" ret")) Puts("");
                }
            }

            private void BuildNode(Node node, int edge_number)
            {
                Puts("BuildNode: " + node.Char + " (" + (int)node.Char + ")");

                // Check the char at the current position
                firstNodeInstructions.Add(AddInstruction(OpCodes.Ldarg_1)); // method_name
                AddInstruction(OpCodes.Ldloc_1);                            // i
                AddInstruction(OpCodes.Callvirt, getChars);                 // method_name[i]
                AddInstruction(Ldc_I4_n(node.Char));
                // If char does not match and there are no more edges to check, return false
                if (node.Parent.Edges.Count <= edge_number) JumpToEnd();

                // Method continuing with this char exist, increment position
                AddInstruction(OpCodes.Ldloc_1);
                AddInstruction(OpCodes.Ldc_I4_1);
                AddInstruction(OpCodes.Add);
                AddInstruction(OpCodes.Stloc_1);

                if (node.Name != null)
                {
                    // Check if we are at the end of the method name
                    AddInstruction(OpCodes.Ldloc_1);
                    AddInstruction(OpCodes.Ldloc_0);
                    // If the method name is longer than the current position
                    JumpToNext();

                    // Method has been found, prepare to call method
                    AddInstruction(OpCodes.Ldarg_2);    // out object ret
                    AddInstruction(OpCodes.Ldarg_0);    // this
                    CallMethod(hookMethods[node.Name]);
                    Return(true);
                }

                var n = 1;
                foreach (var edge in node.Edges.Keys)
                    BuildNode(node.Edges[edge], n++);
            }

            private void CallMethod(MethodDefinition method)
            {
                for (var i = 0; i < method.Parameters.Count; i++)
                {
                    var parameter = method.Parameters[i];
                    AddInstruction(OpCodes.Ldarg_3);    // object[] params
                    AddInstruction(Ldc_I4_n(i));        // param_number
                    AddInstruction(OpCodes.Ldelem_Ref);
                    AddInstruction(OpCodes.Isinst, module.Import(parameter.ParameterType));
                }
                AddInstruction(OpCodes.Call, module.Import(method));
                AddInstruction(OpCodes.Stind_Ref);
            }

            private Instruction Return(bool value)
            {
                var instruction = AddInstruction(Ldc_I4_n(value ? 1 : 0));
                AddInstruction(OpCodes.Ret);
                return instruction;
            }
            
            private void JumpToNext()
            {
                jumpToNextEdgePlaceholders[firstNodeInstructions.Count] = AddInstruction(OpCodes.Bne_Un, body.Instructions[1]);
            }

            private void JumpToEnd()
            {
                jumpToEndPlaceholders.Add(AddInstruction(OpCodes.Bne_Un, body.Instructions[0]));
            }

            private Instruction AddInstruction(OpCode opcode)
            {
                return AddInstruction(Instruction.Create(opcode));
            }

            private Instruction AddInstruction(OpCode opcode, Instruction instruction)
            {
                return AddInstruction(Instruction.Create(opcode, instruction));
            }

            private Instruction AddInstruction(OpCode opcode, MethodReference method_reference)
            {
                return AddInstruction(Instruction.Create(opcode, method_reference));
            }

            private Instruction AddInstruction(OpCode opcode, TypeReference type_reference)
            {
                return AddInstruction(Instruction.Create(opcode, type_reference));
            }

            private Instruction AddInstruction(OpCode opcode, int value)
            {
                return AddInstruction(Instruction.Create(opcode, value));
            }

            private Instruction AddInstruction(Instruction instruction)
            {
                body.Instructions.Add(instruction);
                return instruction;
            }

            private Instruction Ldc_I4_n(int n)
            {
                if (n == 0) return Instruction.Create(OpCodes.Ldc_I4_0);
                if (n == 1) return Instruction.Create(OpCodes.Ldc_I4_1);
                if (n == 2) return Instruction.Create(OpCodes.Ldc_I4_2);
                if (n == 3) return Instruction.Create(OpCodes.Ldc_I4_3);
                if (n == 4) return Instruction.Create(OpCodes.Ldc_I4_4);
                if (n == 5) return Instruction.Create(OpCodes.Ldc_I4_5);
                if (n == 6) return Instruction.Create(OpCodes.Ldc_I4_6);
                if (n == 7) return Instruction.Create(OpCodes.Ldc_I4_7);
                if (n == 8) return Instruction.Create(OpCodes.Ldc_I4_8);
                return Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)n);
            }

            private void UpdateInstructions()
            {
                int offset = 0;
                for (int i = 0; i < body.Instructions.Count; i++)
                {
                    var instruction = body.Instructions[i];
                    if (i == 0)
                        instruction.Previous = null;
                    else
                        instruction.Previous = body.Instructions[i - 1];
                    if (i == body.Instructions.Count - 1)
                        instruction.Next = null;
                    else
                        instruction.Next = body.Instructions[i + 1];
                    instruction.Offset = offset;
                    offset += instruction.GetSize();
                }
            }
        }

        static void Main(string[] args)
        {
            var definition = AssemblyDefinition.ReadAssembly(@"D:\GitHub\HookDispatch\DebugPlugin\bin\Release\DebugPlugin.dll");

            var module = definition.MainModule;
            foreach (var type_definition in module.Types)
            {
                if (type_definition.Namespace == "Oxide.Plugins" && type_definition.Name == "DebugPlugin")
                {
                    var instance = new DirectCallMethod(module, type_definition);

                    AssemblyDefinition final_definition = null;

                    byte[] patched_assembly;
                    using (var stream = new MemoryStream())
                    {
                        definition.Write(stream);
                        patched_assembly = stream.ToArray();
                        stream.Position = 0;
                        final_definition = AssemblyDefinition.ReadAssembly(stream);
                    }
                    
                    Puts("\n================================ Written IL: ================================");
                    var final_type = final_definition.MainModule.GetType("Oxide.Plugins.DebugPlugin");
                    var final_method = final_type.Methods.First(m => m.Name == "DirectCallHook");
                    foreach (var instruction in final_method.Body.Instructions)
                    {
                        Puts(instruction);
                        if ((instruction.OpCode == OpCodes.Bne_Un || instruction.OpCode == OpCodes.Bne_Un_S) && instruction.Operand == null) Puts("                  ^ OPERAND IS MISSING");
                    }

                    var assembly = Assembly.Load(patched_assembly);

                    var type = assembly.GetType("Oxide.Plugins.DebugPlugin");
                    if (type == null)
                    {
                        Puts("Unable to find main plugin class");
                        return;
                    }

                    var plugin = Activator.CreateInstance(type) as Plugin;

                    object ret;
                    try {
                        plugin.DirectCallHook("OnMy", out ret, new[] { "test" });
                        plugin.DirectCallHook("OnMy2", out ret, new[] { "test2" });
                        plugin.DirectCallHook("OnYour", out ret, new[] { "test3" });
                    }
                    catch (Exception ex)
                    {
                        Puts(ex);
                    }

                    Puts("\nPress any key to exit...");
                    Console.ReadKey();
                }
            }
        }

        static void Puts(object obj)
        {
            Console.WriteLine(obj.ToString());
        }
    }
}
