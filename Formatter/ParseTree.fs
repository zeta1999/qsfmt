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

let private findTerminal tokens (context : ParserRuleContext) predicate =
    context.children
    |> Seq.choose (function
        | :? ITerminalNode as terminal -> Some terminal
        | _ -> None)
    |> Seq.tryFind (fun terminal -> terminal.GetText () |> predicate)
    |> Option.map (toTerminalToken tokens)
    |> Option.defaultValue Missing

let private flip f x y = f y x

type private TypeVisitor (tokens) =
    inherit QSharpBaseVisitor<Type Token> ()

    override _.DefaultResult = Missing

    override _.VisitTypeName context = context.GetText () |> TypeName |> toNodeToken tokens context

type private ExpressionVisitor (tokens) =
    inherit QSharpBaseVisitor<Expression Token> ()

    override _.DefaultResult = Missing

    override _.VisitIdentifier context = context.GetText () |> Literal |> toNodeToken tokens context

    override _.VisitInteger context = context.GetText () |> Literal |> toNodeToken tokens context

    override this.VisitTuple context =
        { OpenParen = (=) "(" |> findTerminal tokens context
          Items = context.expression () |> Array.toList |> List.map this.Visit
          CloseParen = (=) ")" |> findTerminal tokens context }
        |> Tuple
        |> toNodeToken tokens context

    override this.VisitAdd context =
        { Left = context.expression 0 |> this.Visit
          Operator = (=) "+" |> findTerminal tokens context
          Right = context.expression 1 |> this.Visit }
        |> BinaryOperator
        |> toNodeToken tokens context

    override this.VisitSubtract context =
        { Left = context.expression 0 |> this.Visit
          Operator = (=) "-" |> findTerminal tokens context
          Right = context.expression 1 |> this.Visit }
        |> BinaryOperator
        |> toNodeToken tokens context

type private SymbolTupleVisitor (tokens) =
    inherit QSharpBaseVisitor<SymbolTuple Token> ()

    override _.DefaultResult = Missing

    override _.VisitSymbol context =
        context.Identifier () |> toTerminalToken tokens |> Symbol |> toNodeToken tokens context

    override this.VisitSymbols context =
        context.symbolTuple ()
        |> Array.toList
        |> List.map this.Visit
        |> Symbols
        |> toNodeToken tokens context

type private StatementVisitor (tokens) =
    inherit QSharpBaseVisitor<Statement Token> ()

    let expressionVisitor = ExpressionVisitor tokens

    let symbolTupleVisitor = SymbolTupleVisitor tokens

    override _.DefaultResult = Missing

    override _.VisitReturn context =
        { ReturnKeyword = (=) "return" |> findTerminal tokens context
          Expression = context.expression () |> expressionVisitor.Visit
          Semicolon = (=) ";" |> findTerminal tokens context }
        |> Return
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

    override _.VisitLet context =
        { LetKeyword = (=) "let" |> findTerminal tokens context
          SymbolTuple = context.symbolTuple () |> symbolTupleVisitor.Visit
          Equals = (=) "=" |> findTerminal tokens context
          Expression = context.expression () |> expressionVisitor.Visit
          Semicolon = (=) ";" |> findTerminal tokens context }
        |> Let
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

type private NamespaceElementVisitor (tokens) =
    inherit QSharpBaseVisitor<NamespaceElement Token> ()

    let typeVisitor = TypeVisitor tokens

    let statementVisitor = StatementVisitor tokens

    override _.DefaultResult = Missing

    override _.VisitCallableDeclaration context =
        let suffix = context.callableDeclarationSuffix ()
        let scope = suffix.callableBody().scope() // TODO
        { CallableKeyword = [ "function"; "operation" ] |> flip List.contains |> findTerminal tokens context
          Name = suffix.Identifier () |> toTerminalToken tokens
          Colon = (=) ":" |> findTerminal tokens suffix
          ReturnType = suffix.``type`` () |> typeVisitor.Visit
          OpenBrace = (=) "{" |> findTerminal tokens scope
          Statements = scope.statement() |> Array.toList |> List.map statementVisitor.Visit
          CloseBrace = (=) "}" |> findTerminal tokens scope }
        |> CallableDeclaration
        |> toNodeToken tokens context
        |> withoutTrailingTrivia

let private toNamespaceToken tokens (context : QSharpParser.NamespaceContext) =
    let visitor = NamespaceElementVisitor tokens
    let name = context.qualifiedName ()
    { NamespaceKeyword = (=) "namespace" |> findTerminal tokens context
      Name = name.GetText () |> Terminal |> toNodeToken tokens name
      OpenBrace = (=) "{" |> findTerminal tokens context
      Elements = context.namespaceElement () |> Array.toList |> List.map visitor.Visit
      CloseBrace = (=) "}" |> findTerminal tokens context }
    |> toNodeToken tokens context
    |> withoutTrailingTrivia

let toProgramToken tokens (context : QSharpParser.ProgramContext) =
    context.``namespace`` ()
    |> Array.toList
    |> List.map (toNamespaceToken tokens)
    |> Program
    |> toNodeToken tokens context
    |> withoutTrailingTrivia
