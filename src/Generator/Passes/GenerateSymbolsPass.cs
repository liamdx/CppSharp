﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CppSharp.AST;
using CppSharp.Parser;
using CppSharp.Utils;

namespace CppSharp.Passes
{
    public class GenerateSymbolsPass : TranslationUnitPass
    {
        public GenerateSymbolsPass()
        {
            VisitOptions.VisitClassBases = false;
            VisitOptions.VisitClassFields = false;
            VisitOptions.VisitEventParameters = false;
            VisitOptions.VisitFunctionParameters = false;
            VisitOptions.VisitFunctionReturnType = false;
            VisitOptions.VisitNamespaceEnums = false;
            VisitOptions.VisitNamespaceEvents = false;
            VisitOptions.VisitNamespaceTemplates = false;
            VisitOptions.VisitNamespaceTypedefs = false;
            VisitOptions.VisitNamespaceVariables = false;
            VisitOptions.VisitTemplateArguments = false;
        }

        public override bool VisitASTContext(ASTContext context)
        {
            var result = base.VisitASTContext(context);
            var findSymbolsPass = Context.TranslationUnitPasses.FindPass<FindSymbolsPass>();
            GenerateSymbols();
            if (remainingCompilationTasks > 0)
                findSymbolsPass.Wait = true;
            return result;
        }

        public event EventHandler<SymbolsCodeEventArgs> SymbolsCodeGenerated;

        private void GenerateSymbols()
        {
            var modules = Options.Modules.Where(symbolsCodeGenerators.ContainsKey).ToList();
            remainingCompilationTasks = modules.Count;
            foreach (var module in modules)
            {
                var symbolsCodeGenerator = symbolsCodeGenerators[module];

                var cpp = $"{module.SymbolsLibraryName}.{symbolsCodeGenerator.FileExtension}";
                Directory.CreateDirectory(Options.OutputDir);
                var path = Path.Combine(Options.OutputDir, cpp);
                File.WriteAllText(path, symbolsCodeGenerator.Generate());

                var e = new SymbolsCodeEventArgs(module);
                SymbolsCodeGenerated?.Invoke(this, e);
                if (string.IsNullOrEmpty(e.CustomCompiler))
                {
                    using (var linkerOptions = new LinkerOptions())
                    {
                        foreach (var libraryDir in module.LibraryDirs)
                            linkerOptions.AddLibraryDirs(libraryDir);

                        foreach (var library in module.Libraries)
                            linkerOptions.AddLibraries(library);

                        using (var result = Parser.ClangParser.Build(
                            Context.ParserOptions, linkerOptions, path))
                            PrintDiagnostics(result);
                    }
                    RemainingCompilationTasks--;
                }
                else
                    InvokeCompiler(e.CustomCompiler, e.CompilerArguments,
                        e.OutputDir, module);
            }
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!base.VisitClassDecl(@class))
                return false;

            if (@class.IsDependent)
                foreach (var specialization in @class.Specializations.Where(
                    s => s.IsExplicitlyGenerated))
                    specialization.Visit(this);
            else
                CheckBasesForSpecialization(@class);

            return true;
        }

        public override bool VisitFunctionDecl(Function function)
        {
            if (!base.VisitFunctionDecl(function))
                return false;

            var module = function.TranslationUnit.Module;

            if (function.IsGenerated)
            {
                ASTUtils.CheckTypeForSpecialization(function.OriginalReturnType.Type,
                    function, Add, Context.TypeMaps);
                foreach (var parameter in function.Parameters)
                    ASTUtils.CheckTypeForSpecialization(parameter.Type, function,
                        Add, Context.TypeMaps);
            }

            if (!NeedsSymbol(function))
                return false;

            var symbolsCodeGenerator = GetSymbolsCodeGenerator(module);
            return function.Visit(symbolsCodeGenerator);
        }

        public class SymbolsCodeEventArgs : EventArgs
        {
            public SymbolsCodeEventArgs(Module module)
            {
                this.Module = module;
            }

            public Module Module { get; set; }
            public string CustomCompiler { get; set; }
            public string CompilerArguments { get; set; }
            public string OutputDir { get; set; }
        }

        private bool NeedsSymbol(Function function)
        {
            var mangled = function.Mangled;
            var method = function as Method;
            bool isInImplicitSpecialization;
            var declarationContext = function.Namespace;
            do
            {
                isInImplicitSpecialization =
                    declarationContext is ClassTemplateSpecialization specialization &&
                    specialization.SpecializationKind !=
                        TemplateSpecializationKind.ExplicitSpecialization;
                declarationContext = declarationContext.Namespace;
            } while (!isInImplicitSpecialization && declarationContext != null);

            return function.IsGenerated && !function.IsDeleted &&
                !function.IsDependent && !function.IsPure && function.Namespace.IsGenerated &&
                (!string.IsNullOrEmpty(function.Body) ||
                 isInImplicitSpecialization || function.IsImplicit) &&
                // we don't need symbols for virtual functions anyway
                (method == null || (!method.IsVirtual && !method.IsSynthetized &&
                 (!method.IsConstructor || !((Class) method.Namespace).IsAbstract))) &&
                // we cannot handle nested anonymous types
                (!(function.Namespace is Class) || !string.IsNullOrEmpty(function.Namespace.OriginalName)) &&
                !Context.Symbols.FindSymbol(ref mangled);
        }

        private SymbolsCodeGenerator GetSymbolsCodeGenerator(Module module)
        {
            if (symbolsCodeGenerators.ContainsKey(module))
                return symbolsCodeGenerators[module];
            
            var symbolsCodeGenerator = new SymbolsCodeGenerator(Context, module.Units);
            symbolsCodeGenerators[module] = symbolsCodeGenerator;
            symbolsCodeGenerator.Process();

            return symbolsCodeGenerator;
        }

        private static void PrintDiagnostics(ParserResult result)
        {
            for (uint i = 0; i < result.DiagnosticsCount; i++)
            {
                var diag = result.GetDiagnostics(i);
                switch (diag.Level)
                {
                    case ParserDiagnosticLevel.Ignored:
                    case ParserDiagnosticLevel.Note:
                        Diagnostics.Message("{0}({1},{2}): {3}: {4}",
                            diag.FileName, diag.LineNumber, diag.ColumnNumber,
                            diag.Level.ToString().ToLower(), diag.Message);
                        break;
                    case ParserDiagnosticLevel.Warning:
                        Diagnostics.Warning("{0}({1},{2}): {3}: {4}",
                            diag.FileName, diag.LineNumber, diag.ColumnNumber,
                            diag.Level.ToString().ToLower(), diag.Message);
                        break;
                    case ParserDiagnosticLevel.Error:
                        Diagnostics.Error("{0}({1},{2}): {3}: {4}",
                            diag.FileName, diag.LineNumber, diag.ColumnNumber,
                            diag.Level.ToString().ToLower(), diag.Message);
                        break;
                    case ParserDiagnosticLevel.Fatal:
                        Diagnostics.Debug("{0}({1},{2}): {3}: {4}",
                            diag.FileName, diag.LineNumber, diag.ColumnNumber,
                            diag.Level.ToString().ToLower(), diag.Message);
                        break;
                }
            }
        }

        private void InvokeCompiler(string compiler, string arguments, string outputDir, Module module)
        {
            new Thread(() =>
                {
                    int error;
                    string errorMessage;
                    ProcessHelper.Run(compiler, arguments, out error, out errorMessage);
                    var output = GetOutputFile(module.SymbolsLibraryName);
                    if (!File.Exists(Path.Combine(outputDir, output)))
                        Diagnostics.Error(errorMessage);
                    else
                        compiledLibraries[module] = new CompiledLibrary
                            { OutputDir = outputDir, Library = module.SymbolsLibraryName };
                    RemainingCompilationTasks--;
                }).Start();
        }

        private void CheckBasesForSpecialization(Class @class)
        {
            foreach (var @base in @class.Bases.Where(b => b.IsClass))
            {
                var specialization = @base.Class as ClassTemplateSpecialization;
                if (specialization != null && !specialization.IsExplicitlyGenerated &&
                    specialization.SpecializationKind != TemplateSpecializationKind.ExplicitSpecialization)
                    ASTUtils.CheckTypeForSpecialization(@base.Type, @class, Add, Context.TypeMaps);
                CheckBasesForSpecialization(@base.Class);
            }
        }

        private void Add(ClassTemplateSpecialization specialization)
        {
            ICollection<ClassTemplateSpecialization> specs;
            if (specializations.ContainsKey(specialization.TranslationUnit.Module))
                specs = specializations[specialization.TranslationUnit.Module];
            else specs = specializations[specialization.TranslationUnit.Module] =
                new HashSet<ClassTemplateSpecialization>();
            if (!specs.Contains(specialization))
            {
                specs.Add(specialization);
                foreach (Method method in specialization.Methods)
                    method.Visit(this);
            }
            GetSymbolsCodeGenerator(specialization.TranslationUnit.Module);
        }

        private int RemainingCompilationTasks
        {
            get { return remainingCompilationTasks; }
            set
            {
                if (remainingCompilationTasks != value)
                {
                    remainingCompilationTasks = value;
                    if (remainingCompilationTasks == 0)
                    {
                        foreach (var module in Context.Options.Modules.Where(compiledLibraries.ContainsKey))
                        {
                            CompiledLibrary compiledLibrary = compiledLibraries[module];
                            CollectSymbols(compiledLibrary.OutputDir, compiledLibrary.Library);
                        }
                        var findSymbolsPass = Context.TranslationUnitPasses.FindPass<FindSymbolsPass>();
                        findSymbolsPass.Wait = false;
                    }
                }
            }
        }

        private void CollectSymbols(string outputDir, string library)
        {
            using (var linkerOptions = new LinkerOptions())
            {
                linkerOptions.AddLibraryDirs(outputDir);
                var output = GetOutputFile(library);
                linkerOptions.AddLibraries(output);
                using (var parserResult = Parser.ClangParser.ParseLibrary(linkerOptions))
                {
                    if (parserResult.Kind == ParserResultKind.Success)
                    {
                        lock (@lock)
                        {
                            for (uint i = 0; i < parserResult.LibrariesCount; i++)
                            {
                                var nativeLibrary = ClangParser.ConvertLibrary(parserResult.GetLibraries(i));
                                Context.Symbols.Libraries.Add(nativeLibrary);
                                Context.Symbols.IndexSymbols();
                            }
                        }
                    }
                    else
                        Diagnostics.Error($"Parsing of {Path.Combine(outputDir, output)} failed.");
                }
            }
        }

        private static string GetOutputFile(string library)
        {
            return Path.GetFileName($@"{(Platform.IsWindows ?
                string.Empty : "lib")}{library}.{
                (Platform.IsMacOS ? "dylib" : Platform.IsWindows ? "dll" : "so")}");
        }

        private int remainingCompilationTasks;
        private static readonly object @lock = new object();

        private Dictionary<Module, SymbolsCodeGenerator> symbolsCodeGenerators =
            new Dictionary<Module, SymbolsCodeGenerator>();
        private Dictionary<Module, HashSet<ClassTemplateSpecialization>> specializations =
            new Dictionary<Module, HashSet<ClassTemplateSpecialization>>();
        private Dictionary<Module, CompiledLibrary> compiledLibraries = new Dictionary<Module, CompiledLibrary>();

        private class CompiledLibrary
        {
            public string OutputDir { get; set; }
            public string Library { get; set; }
        }
    }
}
