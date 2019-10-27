/*
 * MIT License

Copyright (c) 2018 Robin Sue

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

// Modified from https://github.com/Suchiman/Runny as the only working example I could find,
// after I failed to get the Scripting API to run in Blazor.
// It should be possible to either get the Scripting API to work based on this,
// or to get this to provide a minimal Scripting API.

namespace Runny
{
    public static class Compiler
    {
        class BlazorBoot
        {
            public string main { get; set; }
            public string entryPoint { get; set; }
            public string[] assemblyReferences { get; set; }
            public string[] cssReferences { get; set; }
            public string[] jsReferences { get; set; }
            public bool linkerEnabled { get; set; }
        }

        private static Task InitializationTask;
        private static List<MetadataReference> References;
        public static List<string> Imports = new List<string>(); // not used by main API

        public static async Task<ScriptState<object>> ExecScriptAsync(string code)
        { // doesn't work (Could not find file "/mscorlib.dll")
            var options = ScriptOptions.Default
                .WithReferences(References)
                .AddImports(Imports);
            var state = await CSharpScript.RunAsync(code, options);
            return state;
        }

        public static void InitializeMetadataReferences(HttpClient client)
        {
            async Task InitializeInternal()
            {
                var response = await client.GetJsonAsync<BlazorBoot>("_framework/blazor.boot.json");
                var assemblies = await Task.WhenAll(response.assemblyReferences.Where(x => x.EndsWith(".dll")).Select(x => client.GetAsync("_framework/_bin/" + x)));

                var references = new List<MetadataReference>(assemblies.Length);
                foreach (var asm in assemblies)
                {
                    using (var task = await asm.Content.ReadAsStreamAsync())
                    {
                        references.Add(MetadataReference.CreateFromStream(task));
                    }
                }

                References = references;
            }
            InitializationTask = InitializeInternal();
        }

        public static void WhenReady(Func<Task> action)
        {
            if (InitializationTask.Status != TaskStatus.RanToCompletion)
            {
                InitializationTask.ContinueWith(x => action());
            }
            else
            {
                action();
            }
        }

        public static (bool success, Assembly asm) LoadSource(string source)
        {
            var compilation = CSharpCompilation.Create("DynamicCode")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(References)
                .AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)/*.WithKind(SourceCodeKind.Script)*/));
            
            ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();

            bool error = false;
            foreach (Diagnostic diag in diagnostics)
            {
                switch (diag.Severity)
                {
                    case DiagnosticSeverity.Info:
                        Console.WriteLine(diag.ToString());
                        break;
                    case DiagnosticSeverity.Warning:
                        Console.WriteLine(diag.ToString());
                        break;
                    case DiagnosticSeverity.Error:
                        error = true;
                        Console.WriteLine(diag.ToString());
                        break;
                }
            }

            if (error)
            {
                return (false, null);
            }

            using (var outputAssembly = new MemoryStream())
            {
                compilation.Emit(outputAssembly);

                return (true, Assembly.Load(outputAssembly.ToArray()));
            }
        }

        public static string Format(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();
            var normalized = root.NormalizeWhitespace();
            return normalized.ToString();
        }
    }
}
