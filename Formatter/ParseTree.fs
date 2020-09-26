﻿module internal QsFmt.Formatter.ParseTree

open Antlr4.Runtime
open Antlr4.Runtime.Tree
open QsFmt.Formatter.SyntaxTree
open QsFmt.Parser

let private trailingTrivia (tokens : BufferedTokenStream) index =
    tokens.GetHiddenTokensToRight index :> _ seq
    |> Option.ofObj
    |> Option.defaultValue Seq.empty
    |> Seq.map (fun token -> token.Text)
    |> String.concat ""

let private withoutTrailingTrivia = function
    | Node node -> Node { node with TrailingTrivia = "" }
    | Missing -> Missing

let private toNodeToken tokens (context : ParserRuleContext) node =
    { Node = node
      TrailingTrivia = trailingTrivia tokens context.Stop.TokenIndex }
    |> Node

let private toTerminalToken tokens (terminal : ITerminalNode) =
    { Node = terminal.GetText () |> Terminal
      TrailingTrivia = trailingTrivia tokens terminal.Symbol.TokenIndex }
    |> Node

let private findTerminal tokens (context : ParserRuleContext) text =
    context.children
    |> Seq.choose (function
        | :? ITerminalNode as terminal -> Some terminal
        | _ -> None)
    |> Seq.tryFind (fun terminal -> terminal.GetText () = text)
    |> Option.map (toTerminalToken tokens)
    |> Option.defaultValue Missing

type private ExpressionVisitor () =
    inherit QSharpBaseVisitor<Expression Token> ()

    override _.DefaultResult = Missing

let private expressionVisitor = ExpressionVisitor ()

type private SymbolTupleVisitor (tokens) =
    inherit QSharpBaseVisitor<SymbolTuple Token> ()

    override _.DefaultResult = Missing

    override _.VisitSymbol context =
        context.Identifier().GetText() |> Symbol |> toNodeToken tokens context

    override this.VisitSymbols context =
        context.symbolTuple ()
        |> Array.toList
        |> List.map this.Visit
        |> Symbols
        |> toNodeToken tokens context

type private StatementVisitor (tokens) =
    inherit QSharpBaseVisitor<Statement Token> ()

    let symbolTupleVisitor = SymbolTupleVisitor tokens

    override _.DefaultResult = Missing

    override _.VisitReturn context =
        { Expression = context.expression () |> expressionVisitor.Visit
          Semicolon = findTerminal tokens context ";" }
        |> Return
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

    override _.VisitLet context =
        { SymbolTuple = context.symbolTuple () |> symbolTupleVisitor.Visit
          Equals = findTerminal tokens context "="
          Expression = context.expression () |> expressionVisitor.Visit
          Semicolon = findTerminal tokens context ";" }
        |> Let
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

type private NamespaceElementVisitor (tokens) =
    inherit QSharpBaseVisitor<NamespaceElement Token> ()

    let statementVisitor = StatementVisitor tokens

    override _.DefaultResult = Missing

    override _.VisitCallableDeclaration context =
        let scope = context.callableDeclarationSuffix().callableBody().scope() // TODO
        { OpenBrace = findTerminal tokens scope "{"
          Statements = scope.statement() |> Array.toList |> List.map statementVisitor.Visit
          CloseBrace = findTerminal tokens scope "}" }
        |> CallableDeclaration
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

let private toNamespaceToken tokens (context : QSharpParser.NamespaceContext) =
    let visitor = NamespaceElementVisitor tokens
    { OpenBrace = findTerminal tokens context "{"
      Elements = context.namespaceElement () |> Array.toList |> List.map visitor.Visit
      CloseBrace = findTerminal tokens context "}" }
    |> toNodeToken tokens context
    |> withoutTrailingTrivia

let toProgramToken tokens (context : QSharpParser.ProgramContext) =
    context.``namespace`` ()
    |> Array.toList
    |> List.map (toNamespaceToken tokens)
    |> Program
    |> toNodeToken tokens context
    |> withoutTrailingTrivia
