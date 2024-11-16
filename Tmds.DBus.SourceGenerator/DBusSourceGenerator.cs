using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;


namespace Tmds.DBus.SourceGenerator
{
    [Generator]
    public partial class DBusSourceGenerator : IIncrementalGenerator
    {
        private Dictionary<string, MethodDeclarationSyntax> _readMethodExtensions = null!;
        private Dictionary<string, MethodDeclarationSyntax> _writeMethodExtensions = null!;

        private class DBusInterfaceNameEqualityComparer : IEqualityComparer<DBusInterface>
        {
            public bool Equals(DBusInterface? a, DBusInterface? b) => a?.Name == b?.Name;
            public int GetHashCode(DBusInterface? i) => i?.Name?.GetHashCode() ?? 0;
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            _readMethodExtensions = new Dictionary<string, MethodDeclarationSyntax>();
            _writeMethodExtensions = new Dictionary<string, MethodDeclarationSyntax>();

            XmlSerializer xmlSerializer = new(typeof(DBusNode));
            XmlReaderSettings xmlReaderSettings = new()
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
                IgnoreComments = true
            };

            IncrementalValuesProvider<(DBusNode Node, string GeneratorMode)> generatorProvider = context.AdditionalTextsProvider
                .Where(static x => x.Path.EndsWith(".xml", StringComparison.Ordinal))
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select((x, _) =>
                {
                    if (!x.Right.GetOptions(x.Left).TryGetValue("build_metadata.AdditionalFiles.DBusGeneratorMode", out string? generatorMode))
                        return null;
                    if (xmlSerializer.Deserialize(XmlReader.Create(new StringReader(x.Left.GetText()!.ToString()), xmlReaderSettings)) is not DBusNode dBusNode)
                        return null;
                    return dBusNode.Interfaces is null ? null : Tuple.Create(dBusNode, generatorMode);
                })
                .Where(static x => x is not null)
                .Select(static (x, _) => x.ToValueTuple());

            context.RegisterSourceOutput(generatorProvider.Collect(), (productionContext, provider) =>
            {
                if (provider.IsEmpty)
                    return;
                IEnumerable<(DBusInterface DBusInterface, string GeneratorModes)> interfaces = provider
                    .SelectMany(value => value.Node.Interfaces.Select<DBusInterface,(DBusInterface DBusInterface,string GeneratorMode)>(dbusInterface => (dbusInterface, value.GeneratorMode)))
                    .ToLookup(interfaces => interfaces.DBusInterface, interfaces => interfaces.GeneratorMode, new DBusInterfaceNameEqualityComparer())
                    .Select(interfaces => (interfaces.Key, string.Join(",", interfaces)));
                foreach ((DBusInterface DBusInterface, string GeneratorModes) @interface in interfaces)
                {
                    if (@interface.GeneratorModes.Contains("Proxy"))
                    {
                        TypeDeclarationSyntax typeDeclarationSyntax = GenerateProxy(@interface.DBusInterface);
                        NamespaceDeclarationSyntax namespaceDeclaration = NamespaceDeclaration(
                            IdentifierName("Tmds.DBus.SourceGenerator"))
                            .AddMembers(typeDeclarationSyntax);
                        CompilationUnitSyntax compilationUnit = MakeCompilationUnit(namespaceDeclaration);
                        string sourceName = $"Tmds.DBus.SourceGenerator.{Pascalize(@interface.DBusInterface.Name!)}Proxy.g.cs";
                        productionContext.AddSource(sourceName, compilationUnit.GetText(Encoding.UTF8));
                    }
                    if (@interface.GeneratorModes.Contains("Handler"))
                    {
                        TypeDeclarationSyntax typeDeclarationSyntax = GenerateHandler(@interface.DBusInterface);
                        NamespaceDeclarationSyntax namespaceDeclaration = NamespaceDeclaration(
                                IdentifierName("Tmds.DBus.SourceGenerator"))
                            .AddMembers(typeDeclarationSyntax);
                        CompilationUnitSyntax compilationUnit = MakeCompilationUnit(namespaceDeclaration);
                        string sourceName = $"Tmds.DBus.SourceGenerator.{Pascalize(@interface.DBusInterface.Name!)}Handler.g.cs";
                        productionContext.AddSource(sourceName, compilationUnit.GetText(Encoding.UTF8));
                    }
                }

                CompilationUnitSyntax readerExtensions = MakeCompilationUnit(
                    NamespaceDeclaration(
                            IdentifierName("Tmds.DBus.SourceGenerator"))
                    .AddMembers(
                        ClassDeclaration("ReaderExtensions")
                            .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))
                            .WithMembers(
                                List<MemberDeclarationSyntax>(_readMethodExtensions.Values))));

                CompilationUnitSyntax writerExtensions = MakeCompilationUnit(
                    NamespaceDeclaration(
                            IdentifierName("Tmds.DBus.SourceGenerator"))
                    .AddMembers(
                        ClassDeclaration("WriterExtensions")
                            .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))
                            .WithMembers(
                                List<MemberDeclarationSyntax>(_writeMethodExtensions.Values)
                                    .Add(MakeWriteNullableStringMethod())
                                    .Add(MakeWriteObjectPathSafeMethod()))));

                productionContext.AddSource("Tmds.DBus.SourceGenerator.PropertyChanges.cs", PropertyChangesClass);
                productionContext.AddSource("Tmds.DBus.SourceGenerator.SignalHelper.cs", SignalHelperClass);
                productionContext.AddSource("Tmds.DBus.SourceGenerator.PathHandler.cs", PathHandlerClass);
                productionContext.AddSource("Tmds.DBus.SourceGenerator.IDBusInterfaceHandler.cs", DBusInterfaceHandlerInterface);
                productionContext.AddSource("Tmds.DBus.SourceGenerator.ReaderExtensions.cs", readerExtensions.GetText(Encoding.UTF8));
                productionContext.AddSource("Tmds.DBus.SourceGenerator.WriterExtensions.cs", writerExtensions.GetText(Encoding.UTF8));
            });
        }
    }
}
