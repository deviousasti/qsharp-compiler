﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations.Core;
using Microsoft.Quantum.QsCompiler.Transformations.QsCodeOutput;


namespace Microsoft.Quantum.QsCompiler.Transformations.SearchAndReplace
{
    using QsTypeKind = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;
    using QsExpressionKind = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;
    using QsRangeInfo = QsNullable<Tuple<QsPositionInfo, QsPositionInfo>>;


    // routines for finding occurrences of symbols/identifiers

    /// <summary>
    /// Class that allows to walk the syntax tree and find all locations where a certain identifier occurs.
    /// If a set of source file names is given on initialization, the search is limited to callables and specializations in those files.
    /// </summary>
    public class IdentifierReferences
    : SyntaxTreeTransformation<IdentifierReferences.TransformationState>
    {
        public class Location : IEquatable<Location>
        {
            public readonly NonNullable<string> SourceFile;
            /// <summary>
            /// contains the offset of the root node relative to which the statement location is given
            /// </summary>
            public readonly Tuple<int, int> DeclarationOffset;
            /// <summary>
            /// contains the location of the statement containing the symbol relative to the root node
            /// </summary>
            public readonly QsLocation RelativeStatementLocation;
            /// <summary>
            /// contains the range of the symbol relative to the statement position
            /// </summary>
            public readonly Tuple<QsPositionInfo, QsPositionInfo> SymbolRange;

            public Location(NonNullable<string> source, Tuple<int, int> declOffset, QsLocation stmLoc, Tuple<QsPositionInfo, QsPositionInfo> range)
            {
                this.SourceFile = source;
                this.DeclarationOffset = declOffset ?? throw new ArgumentNullException(nameof(declOffset));
                this.RelativeStatementLocation = stmLoc ?? throw new ArgumentNullException(nameof(stmLoc));
                this.SymbolRange = range ?? throw new ArgumentNullException(nameof(range));
            }

            public bool Equals(Location other) =>
                this.SourceFile.Value == other?.SourceFile.Value
                && this.DeclarationOffset == other?.DeclarationOffset
                && this.RelativeStatementLocation == other?.RelativeStatementLocation
                && this.SymbolRange.Item1.Equals(other?.SymbolRange?.Item1)
                && this.SymbolRange.Item2.Equals(other?.SymbolRange?.Item2);

            public override bool Equals(object obj) =>
                this.Equals(obj as Location);

            public override int GetHashCode()
            {
                var (hash, multiplier) = (0x51ed270b, -1521134295);
                if (this.DeclarationOffset != null) hash = (hash * multiplier) + this.DeclarationOffset.GetHashCode();
                if (this.RelativeStatementLocation.Offset != null) hash = (hash * multiplier) + this.RelativeStatementLocation.Offset.GetHashCode();
                if (this.RelativeStatementLocation.Range != null) hash = (hash * multiplier) + this.RelativeStatementLocation.Range.GetHashCode();
                if (this.SymbolRange != null) hash = (hash * multiplier) + this.SymbolRange.GetHashCode();
                return this.SourceFile.Value == null ? hash : (hash * multiplier) + this.SourceFile.Value.GetHashCode();
            }
        }


        /// <summary>
        /// Class used to track the internal state for a transformation that finds all locations where a certain identifier occurs.
        /// If no source file is specified prior to transformation, its name is set to the empty string.
        /// The DeclarationOffset needs to be set prior to transformation, and in particular after defining a source file.
        /// If no defaultOffset is specified upon initialization then only the locations of occurrences within statements are logged.
        /// </summary>
        public class TransformationState
        {
            public Tuple<NonNullable<string>, QsLocation> DeclarationLocation { get; internal set; }
            public ImmutableList<Location> Locations { get; private set; }

            /// <summary>
            /// Whenever DeclarationOffset is set, the current statement offset is set to this default value.
            /// </summary>
            private readonly QsLocation DefaultOffset = null;
            private readonly IImmutableSet<NonNullable<string>> RelevantSourseFiles = null;

            internal bool IsRelevant(NonNullable<string> source) =>
                this.RelevantSourseFiles?.Contains(source) ?? true;


            internal TransformationState(Func<Identifier, bool> trackId,
                QsLocation defaultOffset = null, IImmutableSet<NonNullable<string>> limitToSourceFiles = null)
            {
                this.TrackIdentifier = trackId ?? throw new ArgumentNullException(nameof(trackId));
                this.RelevantSourseFiles = limitToSourceFiles;
                this.Locations = ImmutableList<Location>.Empty;
                this.DefaultOffset = defaultOffset;
            }

            private NonNullable<string> CurrentSourceFile = NonNullable<string>.New("");
            private Tuple<int, int> RootOffset = null;
            internal QsLocation CurrentLocation = null;
            internal readonly Func<Identifier, bool> TrackIdentifier;

            public Tuple<int, int> DeclarationOffset
            {
                internal get => this.RootOffset;
                set
                {
                    this.RootOffset = value ?? throw new ArgumentNullException(nameof(value), "declaration offset cannot be null");
                    this.CurrentLocation = this.DefaultOffset;
                }
            }

            public NonNullable<string> Source
            {
                internal get => this.CurrentSourceFile;
                set
                {
                    this.CurrentSourceFile = value;
                    this.RootOffset = null;
                    this.CurrentLocation = null;
                }
            }

            internal void LogIdentifierLocation(Identifier id, QsRangeInfo range)
            {
                if (this.TrackIdentifier(id) && this.CurrentLocation?.Offset != null && range.IsValue)
                {
                    var idLoc = new Location(this.Source, this.RootOffset, this.CurrentLocation, range.Item);
                    this.Locations = this.Locations.Add(idLoc);
                }
            }

            internal void LogIdentifierLocation(TypedExpression ex)
            {
                if (ex.Expression is QsExpressionKind.Identifier id)
                { this.LogIdentifierLocation(id.Item1, ex.Range); }
            }
        }

        public IdentifierReferences(TransformationState state) 
        : base(state, TransformationOptions.NoRebuild)
        { 
            this.Types = new TypeTransformation(this);
            this.Expressions = new TypedExpressionWalker<TransformationState>(this.SharedState.LogIdentifierLocation, this);
            this.Statements = new StatementTransformation(this);
            this.Namespaces = new NamespaceTransformation(this);
        }

        public IdentifierReferences(NonNullable<string> idName, QsLocation defaultOffset, IImmutableSet<NonNullable<string>> limitToSourceFiles = null) 
        : this(new TransformationState(id => id is Identifier.LocalVariable varName && varName.Item.Value == idName.Value, defaultOffset, limitToSourceFiles)) { }

        public IdentifierReferences(QsQualifiedName idName, QsLocation defaultOffset, IImmutableSet<NonNullable<string>> limitToSourceFiles = null) 
        : this(new TransformationState(id => id is Identifier.GlobalCallable cName && cName.Item.Equals(idName), defaultOffset, limitToSourceFiles))
        {
            if (idName == null) throw new ArgumentNullException(nameof(idName));
        }


        // static methods for convenience

        public static IEnumerable<Location> Find(NonNullable<string> idName, QsScope scope,
            NonNullable<string> sourceFile, Tuple<int, int> rootLoc)
        {
            var finder = new IdentifierReferences(idName, null, ImmutableHashSet.Create(sourceFile));
            finder.SharedState.Source = sourceFile;
            finder.SharedState.DeclarationOffset = rootLoc; // will throw if null
            finder.Statements.OnScope(scope ?? throw new ArgumentNullException(nameof(scope)));
            return finder.SharedState.Locations;
        }

        public static IEnumerable<Location> Find(QsQualifiedName idName, QsNamespace ns, QsLocation defaultOffset,
            out Tuple<NonNullable<string>, QsLocation> declarationLocation, IImmutableSet<NonNullable<string>> limitToSourceFiles = null)
        {
            var finder = new IdentifierReferences(idName, defaultOffset, limitToSourceFiles);
            finder.Namespaces.OnNamespace(ns ?? throw new ArgumentNullException(nameof(ns)));
            declarationLocation = finder.SharedState.DeclarationLocation;
            return finder.SharedState.Locations;
        }


        // helper classes

        private class TypeTransformation 
        : TypeTransformation<TransformationState>
        {
            public TypeTransformation(SyntaxTreeTransformation<TransformationState> parent) 
            : base(parent, TransformationOptions.NoRebuild) { }

            public override QsTypeKind OnUserDefinedType(UserDefinedType udt)
            {
                var id = Identifier.NewGlobalCallable(new QsQualifiedName(udt.Namespace, udt.Name));
                this.SharedState.LogIdentifierLocation(id, udt.Range);
                return QsTypeKind.NewUserDefinedType(udt);
            }

            public override QsTypeKind OnTypeParameter(QsTypeParameter tp)
            {
                var resT = ResolvedType.New(QsTypeKind.NewTypeParameter(tp));
                var id = Identifier.NewLocalVariable(NonNullable<string>.New(SyntaxTreeToQsharp.Default.ToCode(resT) ?? ""));
                this.SharedState.LogIdentifierLocation(id, tp.Range);
                return resT.Resolution;
            }
        }

        private class StatementTransformation 
        : StatementTransformation<TransformationState>
        {
            public StatementTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent, TransformationOptions.NoRebuild) { }

            public override QsNullable<QsLocation> OnLocation(QsNullable<QsLocation> loc)
            {
                this.SharedState.CurrentLocation = loc.IsValue ? loc.Item : null;
                return loc;
            }
        }

        private class NamespaceTransformation 
        : NamespaceTransformation<TransformationState>
        {

            public NamespaceTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent, TransformationOptions.NoRebuild) { }

            public override QsCustomType OnTypeDeclaration(QsCustomType t)
            {
                if (!this.SharedState.IsRelevant(t.SourceFile) || t.Location.IsNull) return t;
                if (this.SharedState.TrackIdentifier(Identifier.NewGlobalCallable(t.FullName)))
                { this.SharedState.DeclarationLocation = new Tuple<NonNullable<string>, QsLocation>(t.SourceFile, t.Location.Item); }
                return base.OnTypeDeclaration(t);
            }

            public override QsCallable OnCallableDeclaration(QsCallable c)
            {
                if (!this.SharedState.IsRelevant(c.SourceFile) || c.Location.IsNull) return c;
                if (this.SharedState.TrackIdentifier(Identifier.NewGlobalCallable(c.FullName)))
                { this.SharedState.DeclarationLocation = new Tuple<NonNullable<string>, QsLocation>(c.SourceFile, c.Location.Item); }
                return base.OnCallableDeclaration(c);
            }

            public override QsDeclarationAttribute OnAttribute(QsDeclarationAttribute att)
            {
                var declRoot = this.SharedState.DeclarationOffset;
                this.SharedState.DeclarationOffset = att.Offset;
                if (att.TypeId.IsValue) this.Transformation.Types.OnUserDefinedType(att.TypeId.Item);
                this.Transformation.Expressions.OnTypedExpression(att.Argument);
                this.SharedState.DeclarationOffset = declRoot;
                return att;
            }

            public override QsSpecialization OnSpecializationDeclaration(QsSpecialization spec) =>
                this.SharedState.IsRelevant(spec.SourceFile) ? base.OnSpecializationDeclaration(spec) : spec;

            public override QsNullable<QsLocation> OnLocation(QsNullable<QsLocation> loc)
            {
                this.SharedState.DeclarationOffset = loc.IsValue ? loc.Item.Offset : null;
                return loc;
            }

            public override NonNullable<string> OnSourceFile(NonNullable<string> source)
            {
                this.SharedState.Source = source;
                return source;
            }
        }
    }


    // routines for finding all symbols/identifiers

    /// <summary>
    /// Generates a look-up for all used local variables and their location in any of the transformed scopes,
    /// as well as one for all local variables reassigned in any of the transformed scopes and their locations.
    /// Note that the location information is relative to the root node, i.e. the start position of the containing specialization declaration.
    /// </summary>
    public class AccumulateIdentifiers
    : SyntaxTreeTransformation<AccumulateIdentifiers.TransformationState>
    {
        public class TransformationState
        {
            internal QsLocation StatementLocation = null;
            internal Func<TypedExpression, TypedExpression> UpdatedExpression;

            private readonly List<(NonNullable<string>, QsLocation)> UpdatedLocals = new List<(NonNullable<string>, QsLocation)>();
            private readonly List<(NonNullable<string>, QsLocation)> UsedLocals = new List<(NonNullable<string>, QsLocation)>();

            internal TransformationState() =>
                this.UpdatedExpression = new TypedExpressionWalker<TransformationState>(this.UpdatedLocal, this).OnTypedExpression;

            public ILookup<NonNullable<string>, QsLocation> ReassignedVariables =>
                this.UpdatedLocals.ToLookup(var => var.Item1, var => var.Item2);

            public ILookup<NonNullable<string>, QsLocation> UsedLocalVariables =>
                this.UsedLocals.ToLookup(var => var.Item1, var => var.Item2);


            private Action<TypedExpression> Add(List<(NonNullable<string>, QsLocation)> accumulate) => (TypedExpression ex) =>
            {
                if (ex.Expression is QsExpressionKind.Identifier id &&
                    id.Item1 is Identifier.LocalVariable var)
                {
                    var range = ex.Range.IsValue ? ex.Range.Item : this.StatementLocation.Range;
                    accumulate.Add((var.Item, new QsLocation(this.StatementLocation.Offset, range)));
                }
            };

            internal Action<TypedExpression> UsedLocal => Add(this.UsedLocals);
            internal Action<TypedExpression> UpdatedLocal => Add(this.UpdatedLocals);
        }


        public AccumulateIdentifiers() 
        : base(new TransformationState(), TransformationOptions.NoRebuild)
        {
            this.Statements = new StatementTransformation(this);
            this.StatementKinds = new StatementKindTransformation(this);
            this.Expressions = new TypedExpressionWalker<TransformationState>(this.SharedState.UsedLocal, this);
            this.Types = new TypeTransformation<TransformationState>(this, TransformationOptions.Disabled);
        }


        // helper classes

        private class StatementTransformation 
        : StatementTransformation<TransformationState>
        {
            public StatementTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent, TransformationOptions.NoRebuild) { }

            public override QsStatement OnStatement(QsStatement stm)
            {
                this.SharedState.StatementLocation = stm.Location.IsNull ? null : stm.Location.Item;
                this.StatementKinds.OnStatementKind(stm.Statement);
                return stm;
            }
        }

        private class StatementKindTransformation 
        : StatementKindTransformation<TransformationState>
        {
            public StatementKindTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent, TransformationOptions.NoRebuild) { }

            public override QsStatementKind OnValueUpdate(QsValueUpdate stm)
            {
                this.SharedState.UpdatedExpression(stm.Lhs);
                this.Expressions.OnTypedExpression(stm.Rhs);
                return QsStatementKind.NewQsValueUpdate(stm);
            }
        }
    }


    // routines for replacing symbols/identifiers

    /// <summary>
    /// Upon transformation, assigns each defined variable a unique name, independent on the scope, and replaces all references to it accordingly.
    /// The original variable name can be recovered by using the static method StripUniqueName.
    /// This class is *not* threadsafe.
    /// </summary>
    public class UniqueVariableNames
    : SyntaxTreeTransformation<UniqueVariableNames.TransformationState>
    {
        public class TransformationState
        {
            private int VariableNr = 0;
            private Dictionary<NonNullable<string>, NonNullable<string>> UniqueNames =
                new Dictionary<NonNullable<string>, NonNullable<string>>();

            internal QsExpressionKind AdaptIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs) =>
                sym is Identifier.LocalVariable varName && this.UniqueNames.TryGetValue(varName.Item, out var unique)
                    ? QsExpressionKind.NewIdentifier(Identifier.NewLocalVariable(unique), tArgs)
                    : QsExpressionKind.NewIdentifier(sym, tArgs);

            /// <summary>
            /// Will overwrite the dictionary entry mapping a variable name to the corresponding unique name if the key already exists.
            /// </summary>
            internal NonNullable<string> GenerateUniqueName(NonNullable<string> varName)
            {
                var unique = NonNullable<string>.New($"__{Prefix}{this.VariableNr++}__{varName.Value}__");
                this.UniqueNames[varName] = unique;
                return unique;
            }
        }


        private const string Prefix = "qsVar";
        private const string OrigVarName = "origVarName";
        private static readonly Regex WrappedVarName = new Regex($"^__{Prefix}[0-9]*__(?<{OrigVarName}>.*)__$");

        public UniqueVariableNames() 
        : base(new TransformationState())
        {
            this.StatementKinds = new StatementKindTransformation(this);
            this.ExpressionKinds = new ExpressionKindTransformation(this);
            this.Types = new TypeTransformation<TransformationState>(this, TransformationOptions.Disabled);
        }


        // static methods for convenience

        internal static QsQualifiedName PrependGuid(QsQualifiedName original) =>
            new QsQualifiedName(original.Namespace, NonNullable<string>.New("_" + Guid.NewGuid().ToString("N") + "_" + original.Name.Value));

        public static NonNullable<string> StripUniqueName(NonNullable<string> uniqueName)
        {
            var matched = WrappedVarName.Match(uniqueName.Value).Groups[OrigVarName];
            return matched.Success ? NonNullable<string>.New(matched.Value) : uniqueName;
        }


        // helper classes

        private class StatementKindTransformation
        : StatementKindTransformation<TransformationState>
        {
            public StatementKindTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent) { }

            public override SymbolTuple OnSymbolTuple(SymbolTuple syms) =>
                syms is SymbolTuple.VariableNameTuple tuple
                    ? SymbolTuple.NewVariableNameTuple(tuple.Item.Select(this.OnSymbolTuple).ToImmutableArray())
                    : syms is SymbolTuple.VariableName varName
                    ? SymbolTuple.NewVariableName(this.SharedState.GenerateUniqueName(varName.Item))
                    : syms;
        }

        private class ExpressionKindTransformation
        : ExpressionKindTransformation<TransformationState>
        {
            public ExpressionKindTransformation(SyntaxTreeTransformation<TransformationState> parent)
            : base(parent) { }

            public override QsExpressionKind OnIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs) =>
                this.SharedState.AdaptIdentifier(sym, tArgs);
        }
    }


    // general purpose helpers

    /// <summary>
    /// Upon transformation, applies the specified action to each expression and subexpression.
    /// The action to apply is specified upon construction, and will be applied before recurring into subexpressions.
    /// The transformation merely walks expressions and rebuilding is disabled. 
    /// </summary>
    public class TypedExpressionWalker<T> 
    : ExpressionTransformation<T>
    {
        public TypedExpressionWalker(Action<TypedExpression> onExpression, SyntaxTreeTransformation<T> parent)
        : base(parent, TransformationOptions.NoRebuild) =>
            this.OnExpression = onExpression ?? throw new ArgumentNullException(nameof(onExpression));

        public TypedExpressionWalker(Action<TypedExpression> onExpression, T internalState = default)
        : base(internalState, TransformationOptions.NoRebuild) =>
            this.OnExpression = onExpression ?? throw new ArgumentNullException(nameof(onExpression));

        public readonly Action<TypedExpression> OnExpression;
        public override TypedExpression OnTypedExpression(TypedExpression ex)
        {
            this.OnExpression(ex);
            return base.OnTypedExpression(ex);
        }
    }
}
