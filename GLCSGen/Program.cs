﻿/*
 * Copyright (c) 2013, Nick Gravelyn.
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 *    1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would be
 *    appreciated but is not required.
 *
 *    2. Altered source versions must be plainly marked as such, and must not be
 *    misrepresented as being the original software.
 *
 *    3. This notice may not be removed or altered from any source
 *    distribution.
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GLCSGen
{
    public class Program
    {
        static void Main(string[] args)
        {
            // Isolated mode generates files like we always have, with each C# file being completely standalone
            // with its own set of delegates under the hood. Without isolated mode, the new default is to generate
            // a single interop type per assembly and have each GL version share those function pointers. This
            // reduces assembly size by removing lots and lots of duplicate delegate types and function pointers.
            bool isolated = args.Contains("--isolated");

            // Since error handling code adds size to the generated assemblies, there is now an option to remove
            // it for people who want extremely trim assemblies and don't mind dealing with harder-to-read error
            // messages when function pointers fail to load.
            bool errorHandling = !args.Contains("--no-error-check");

            // Walk up to the solution file so we can then go into GL-CS and write to the C# files directly
            DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GL-CS.sln")))
            {
                directory = directory.Parent;
            }

            // If the solution wasn't found (maybe we're not running from the bin directory)
            // we'll just write out to the current directory
            if (directory == null)
            {
                directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            }

            // Create our two directories for output files
            var glDirectory = new DirectoryInfo(Path.Combine(directory.FullName, "GL-CS"));
            var glesDirectory = new DirectoryInfo(Path.Combine(directory.FullName, "GLES-CS"));

            // Load the spec
            var spec = GLSpec.FromFile(Path.Combine(directory.FullName, "GLCSGen", "gl.xml"));

            //using (var stream = File.Create("spec.xml"))
            //{
            //    var serializer = new XmlSerializer(typeof(GLSpec));
            //    serializer.Serialize(stream, spec);
            //}

            // In non-isolated mode we have to build up a list of all the commands in all versions of
            // the module, and write them into a single GLInterop type.
            if (!isolated)
            {
                CreateGLInterop(spec, glDirectory, "GL");
                CreateGLInterop(spec, glesDirectory, "GLES");
            }

            foreach (var version in spec.Versions)
            {
                var dir = version.Api == "GL" ? glDirectory : glesDirectory;

                using (var writer = new CodeWriter(Path.Combine(dir.FullName, version.Name + ".cs")))
                {
                    WriteFileHeader(writer, spec);
                    writer.WriteOpenBrace();
                    writer.WriteLine("public static class {0}", version.Name);
                    writer.WriteOpenBrace();

                    writer.WriteLine("#region Enums");
                    foreach (var e in version.Enums)
                    {
                        string type = IsUint(e.Value) ? "uint" : "ulong";
                        writer.WriteLine("public static {0} {1} = {2};", type, e.Key, e.Value);
                    }
                    writer.WriteLine("#endregion");

                    writer.WriteLine();

                    writer.WriteLine("#region Commands");
                    foreach (var c in version.Commands)
                    {
                        StringBuilder builder = new StringBuilder("public static ");
                        builder.Append(ConvertGLType(c.ReturnType));
                        builder.AppendFormat(" {0}(", c.Name);
                        BuildParameterList(c, builder);
                        builder.Append(")");
                        writer.WriteLine(builder.ToString());

                        writer.WriteOpenBrace();

                        builder.Clear();
                        if (c.ReturnType != "void")
                        {
                            builder.Append("return ");
                        }
                        if (!isolated)
                        {
                            builder.Append(version.Api);
                            builder.Append("Interop.");
                        }
                        builder.AppendFormat("{0}Ptr(", c.Name);

                        if (c.Parameters.Count > 0)
                        {
                            foreach (var p in c.Parameters)
                            {
                                var name = p.Name;

                                // Add @ to start of any names that are C# keywords to avoid conflict
                                if (name == "params" || name == "string" || name == "ref" || name == "base")
                                {
                                    name = "@" + name;
                                }

                                builder.AppendFormat("{0}, ", name);
                            }
                            builder.Length -= 2;
                        }

                        builder.Append(");");
                        writer.WriteLine(builder.ToString());

                        writer.WriteCloseBrace();
                    }
                    writer.WriteLine("#endregion");

                    if (isolated)
                    {
                        writer.WriteLine();

                        writer.WriteLine("#region Command Delegates");
                        WriteDelegates(writer, "private", version.Commands);
                        writer.WriteLine("#endregion");
                    }

                    writer.WriteLine();
                    writer.WriteLine("#region Interop");
                    writer.WriteLine("public static Func<string, IntPtr> GetProcAddress = null;");
                    writer.WriteLine();
                    writer.WriteLine("public static void LoadAllFunctions()");
                    writer.WriteOpenBrace();
                    foreach (var c in version.Commands)
                    {
                        var delegateName = isolated ? c.Name : (version.Api + "Interop." + c.Name);
                        string getFuncPtrCode = string.Format("{0}Ptr = ({0}Func)Marshal.GetDelegateForFunctionPointer(GetProcAddress(\"{1}\"), typeof({0}Func));", delegateName, c.Name);

                        if (errorHandling)
                        {
                            writer.WriteLine("try {{ {0} }}", getFuncPtrCode);
                            writer.WriteLine("catch {{ throw new InvalidOperationException(\"Failed to get function pointer for '{0}'.\"); }}", c.Name);
                        }
                        else
                        {
                            writer.WriteLine(getFuncPtrCode);
                        }
                    }
                    writer.WriteCloseBrace();
                    writer.WriteLine();
                    writer.WriteLine("public static void LoadFunction(string name)");
                    writer.WriteOpenBrace();
                    if (errorHandling)
                    {
                        writer.WriteLine("try");
                        writer.WriteOpenBrace();
                    }
                    writer.WriteLine("var memberInfo = typeof({0}).GetField(name + \"Ptr\", BindingFlags.{1} | BindingFlags.Static);", isolated ? version.Name : (version.Api + "Interop"), isolated ? "NonPublic" : "Public");
                    if (errorHandling)
                    {
                        writer.WriteLine("Debug.Assert(memberInfo != null, string.Format(\"Failed to find function delegate. Ensure '{0}' is a valid OpenGL function.\", name));");
                    }
                    writer.WriteLine("var procAddr = GetProcAddress(name);");
                    if (errorHandling)
                    {
                        writer.WriteLine("Debug.Assert(procAddr != IntPtr.Zero, string.Format(\"Failed to find function address. Ensure '{0}' is a valid OpenGL function.\", name));");
                    }
                    writer.WriteLine("var funcPtr = Marshal.GetDelegateForFunctionPointer(procAddr, memberInfo.FieldType);");
                    if (errorHandling)
                    {
                        writer.WriteLine("Debug.Assert(funcPtr != null, string.Format(\"Failed to convert function address to delegate for '{0}'.\", name));");
                    }
                    writer.WriteLine("memberInfo.SetValue(null, funcPtr);");
                    if (errorHandling)
                    {
                        writer.WriteCloseBrace();
                        writer.WriteLine("catch");
                        writer.WriteOpenBrace();
                        writer.WriteLine("throw new InvalidOperationException(string.Format(\"Failed to load function '{0}'.\", name));");
                        writer.WriteCloseBrace();
                    }
                    writer.WriteCloseBrace();
                    writer.WriteLine("#endregion");

                    writer.WriteCloseBrace();
                    writer.WriteCloseBrace();
                }
            }
        }

        private static void CreateGLInterop(GLSpec spec, DirectoryInfo outDir, string api)
        {
            // Get the unique list of all commands
            List<GLCommand> commands = new List<GLCommand>();
            foreach (var version in spec.Versions)
            {
                if (version.Api != api)
                {
                    continue;
                }

                foreach (var c in version.Commands)
                {
                    if (commands.Find(c2 => c2.Name == c.Name) == null)
                    {
                        commands.Add(c);
                    }
                }
            }

            // Just to be pretty, sort them by name :)
            commands.Sort((c1, c2) => c1.Name.CompareTo(c2.Name));

            // Create the interop file containing all of the delegate types and instances
            using (var writer = new CodeWriter(Path.Combine(outDir.FullName, api + "Interop.cs")))
            {
                WriteFileHeader(writer, spec);
                writer.WriteOpenBrace();
                writer.WriteLine("internal static class {0}Interop", api);
                writer.WriteOpenBrace();
                WriteDelegates(writer, "public", commands);
                writer.WriteCloseBrace();
                writer.WriteCloseBrace();
            }
        }

        private static void WriteFileHeader(CodeWriter writer, GLSpec spec)
        {
            writer.WriteLine("// This file was autogenerated by GLCSGen on {0} UTC", DateTime.UtcNow);
            writer.WriteLine("// Original copyright from gl.xml:");
            foreach (var l in spec.HeaderComment.Split('\n', '\r'))
            {
                writer.WriteLine("// {0}", l);
            }
            writer.WriteLine();
            writer.WriteLine("using System;");
            writer.WriteLine("using System.Diagnostics;");
            writer.WriteLine("using System.Reflection;");
            writer.WriteLine("using System.Runtime.InteropServices;");
            writer.WriteLine();
            writer.WriteLine("namespace OpenGL");
        }

        private static void WriteDelegates(CodeWriter writer, string accessModifier, IEnumerable<GLCommand> commands)
        {
            foreach (var c in commands)
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendFormat("{0} delegate ", accessModifier);
                builder.Append(ConvertGLType(c.ReturnType));
                builder.AppendFormat(" {0}Func(", c.Name);
                BuildParameterList(c, builder);
                builder.Append(");");

                writer.WriteLine(builder.ToString());
                writer.WriteLine("{0} static {1}Func {1}Ptr;", accessModifier, c.Name);
            }
        }

        private static void BuildParameterList(GLCommand c, StringBuilder builder)
        {
            if (c.Parameters.Count > 0)
            {
                foreach (var p in c.Parameters)
                {
                    var name = p.Name;

                    // Add @ to start of any names that are C# keywords to avoid conflict
                    if (name == "params" || name == "string" || name == "ref" || name == "base")
                    {
                        name = "@" + name;
                    }

                    builder.AppendFormat("{0} {1}, ", ConvertGLType(p.Type), name);
                }
                builder.Length -= 2;
            }
        }

        private static string ConvertGLType(string type)
        {
            if (type == "GLboolean")
            {
                return "bool";
            }
            else if (type == "GLuint" || type == "GLenum" || type == "GLbitfield")
            {
                return "uint";
            }
            else if (type == "GLint" || type == "GLsizei" || type == "GLsizeiptr" || type == "GLfixed" || type == "GLclampx" || type == "GLintptrARB" || type == "GLsizeiptrARB")
            {
                return "int";
            }
            else if (type.Contains("*") || type == "GLsync" || type == "GLintptr" || type == "GLDEBUGPROC")
            {
                return "IntPtr";
            }
            else if (type == "GLfloat" || type == "GLclampf")
            {
                return "float";
            }
            else if (type == "GLdouble")
            {
                return "double";
            }
            else if (type == "GLubyte")
            {
                return "byte";
            }
            else if (type == "GLbyte")
            {
                return "sbyte";
            }
            else if (type == "GLushort")
            {
                return "ushort";
            }
            else if (type == "GLshort")
            {
                return "short";
            }
            else if (type == "GLuint64")
            {
                return "ulong";
            }

            return type;
        }

        private static bool IsUint(string value)
        {
            bool isHex = false;

            if (value.StartsWith("0x"))
            {
                isHex = true;
                value = value.Substring(2);

                if (value.Length > 8)
                {
                    return false;
                }
            }

            uint result;
            if (isHex)
            {
                return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
            }
            else
            {
                return uint.TryParse(value, out result);
            }
        }
    }
}
