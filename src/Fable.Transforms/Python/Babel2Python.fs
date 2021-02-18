module Fable.Transforms.Babel2Python

open System
open System.Collections.Generic
open System.Text.RegularExpressions

open Fable
open Fable.AST
open Fable.AST.Python
open Fable.AST.Babel

[<RequireQualifiedAccess>]
type ReturnStrategy =
    | Return
    | NoReturn
    | NoBreak // Used in switch statement blocks

type ITailCallOpportunity =
    abstract Label: string
    abstract Args: string list
    abstract IsRecursiveRef: Fable.Expr -> bool

type UsedNames =
    { RootScope: HashSet<string>
      DeclarationScopes: HashSet<string>
      CurrentDeclarationScope: HashSet<string> }

type Context =
    {
      //UsedNames: UsedNames
      DecisionTargets: (Fable.Ident list * Fable.Expr) list
      HoistVars: Fable.Ident list -> bool
      TailCallOpportunity: ITailCallOpportunity option
      OptimizeTailCall: unit -> unit
      ScopedTypeParams: Set<string> }

type IPythonCompiler =
    inherit Compiler
    abstract GetAllImports: unit -> Python.Statement list

    abstract GetImportExpr: Context * name: string * moduleName: string * SourceLocation option -> Python.Identifier option

    abstract TransformAsExpr: Context * Babel.Expression -> Python.Expression * Python.Statement list
    abstract TransformAsStatements: Context * ReturnStrategy * Babel.Expression -> Python.Statement list
    abstract TransformAsStatements: Context * ReturnStrategy * Babel.Statement -> Python.Statement list
    abstract TransformAsStatements: Context * ReturnStrategy * Babel.BlockStatement -> Python.Statement list

    abstract TransformAsClassDef:
        Context
        * Babel.ClassBody
        * Babel.Identifier option
        * Babel.Expression option
        * Babel.ClassImplements array option
        * Babel.TypeParameterInstantiation option
        * Babel.TypeParameterDeclaration option
        * SourceLocation option ->
        Python.Statement list

    abstract TransformAsImports: Context * Babel.ImportSpecifier array * Babel.StringLiteral -> Python.Statement list

    abstract TransformFunction: Context * Babel.Identifier * Babel.Pattern array * Babel.BlockStatement -> Python.Statement

    abstract WarnOnlyOnce: string * ?range: SourceLocation -> unit

module Helpers =
    let index = (Seq.initInfinite id).GetEnumerator()

    let getIdentifier (name: string): Python.Identifier =
        do index.MoveNext() |> ignore
        let idx = index.Current.ToString()
        Python.Identifier($"{name}_{idx}")


    /// Replaces all '$' with '_'
    let cleanNameAsPythonIdentifier (name: string) =
        match name with
        | "this" -> "self" // TODO: Babel should use ThisExpression to avoid this hack.
        | "async" -> "asyncio"
        | _ -> name.Replace('$', '_').Replace('.', '_')

    let rewriteFableImport moduleName =
        let _reFableLib =
            Regex(".*\/fable-library[\.0-9]*\/(?<module>[^\/]*)\.js", RegexOptions.Compiled)

        let m = _reFableLib.Match(moduleName)

        if m.Groups.Count > 1 then
            let pymodule =
                m.Groups.["module"].Value.ToLower()
                |> cleanNameAsPythonIdentifier

            let moduleName = String.concat "." [ "fable"; pymodule ]

            moduleName
        else
            // TODO: Can we expect all modules to be lower case?
            let moduleName = moduleName.Replace("/", "").ToLower()
            printfn "moduleName: %s" moduleName
            moduleName

    let unzipArgs (args: (Python.Expression * Python.Statement list) list): Python.Expression list * Python.Statement list =
        let stmts = args |> List.map snd |> List.collect id
        let args = args |> List.map fst
        args, stmts

    /// A few statements in the generated Babel AST do not produce any effect, and will not be printet. But they are
    /// left in the AST and we need to skip them since they are not valid for Python (either).
    let isProductiveStatement (stmt: Python.Statement) =
        let rec hasNoSideEffects (e: Python.Expression) =
            printfn $"hasNoSideEffects: {e}"

            match e with
            | Constant _ -> true
            | Dict { Keys = keys } -> keys.IsEmpty // Empty object
            | Name _ -> true // E.g `void 0` is translated to Name(None)
            | _ -> false

        match stmt with
        | Expr expr ->
            if hasNoSideEffects expr.Value then
                None
            else
                Some stmt
        | _ -> Some stmt

module Util =
    let makeImportTypeId (com: IPythonCompiler) ctx moduleName typeName =
        let expr =
            com.GetImportExpr(ctx, typeName, getLibPath com moduleName, None)

        match expr with
        | Some (id) -> id
        | _ -> Python.Identifier typeName

    let rec transformBody (returnStrategy: ReturnStrategy) (body: Python.Statement list) =
        let body = body |> List.choose Helpers.isProductiveStatement

        match body, returnStrategy with
        | [], ReturnStrategy.Return -> [ Statement.return' () ]
        | [], ReturnStrategy.NoBreak
        | [], ReturnStrategy.NoReturn -> [ Pass ]
        | xs, ReturnStrategy.NoBreak ->
            xs
            |> List.filter (fun x -> x <> Break)
            |> transformBody ReturnStrategy.NoReturn
        | _ -> body

    let transformAsImports
        (com: IPythonCompiler)
        (ctx: Context)
        (specifiers: Babel.ImportSpecifier array)
        (source: Babel.StringLiteral)
        : Python.Statement list =
        let (StringLiteral (value = value)) = source
        let pymodule = value |> Helpers.rewriteFableImport

        printfn "Module: %A" pymodule

        let imports: ResizeArray<Alias> = ResizeArray()
        let importFroms = ResizeArray<Alias>()

        for expr in specifiers do
            match expr with
            | Babel.ImportMemberSpecifier (local, imported) ->
                printfn "ImportMemberSpecifier"

                let alias =
                    Alias.alias (
                        Python.Identifier(imported.Name),
                        if imported.Name <> local.Name then
                            Python.Identifier(local.Name) |> Some
                        else
                            None
                    )

                importFroms.Add(alias)
            | Babel.ImportDefaultSpecifier (local) ->
                printfn "ImportDefaultSpecifier"

                let alias =
                    Alias.alias (
                        Python.Identifier(pymodule),
                        if local.Name <> pymodule then
                            Python.Identifier(local.Name) |> Some
                        else
                            None
                    )

                imports.Add(alias)
            | Babel.ImportNamespaceSpecifier (Identifier (name = name)) ->
                printfn "ImportNamespaceSpecifier: %A" (name, name)

                let alias =
                    Alias.alias (
                        Python.Identifier(pymodule),
                        if pymodule <> name then
                            Python.Identifier(name) |> Some
                        else
                            None
                    )

                importFroms.Add(alias)

        [ if imports.Count > 0 then
              Statement.import (imports |> List.ofSeq)

          if importFroms.Count > 0 then
              Statement.importFrom (Some(Python.Identifier(pymodule)), importFroms |> List.ofSeq) ]

    let transformAsClassDef
        (com: IPythonCompiler)
        (ctx: Context)
        (body: Babel.ClassBody)
        (id: Babel.Identifier option)
        (superClass: Babel.Expression option)
        (implements: Babel.ClassImplements array option)
        (superTypeParameters: Babel.TypeParameterInstantiation option)
        (typeParameters: Babel.TypeParameterDeclaration option)
        (loc: SourceLocation option)
        : Python.Statement list =
        printfn $"transformAsClassDef"

        let bases, stmts =
            let entries =
                superClass
                |> Option.map (fun expr -> com.TransformAsExpr(ctx, expr))

            match entries with
            | Some (expr, stmts) -> [ expr ], stmts
            | None -> [], []

        let body: Python.Statement list =
            [ let (ClassBody (body = body)) = body

              for mber in body do
                  match mber with
                  | Babel.ClassMember.ClassMethod (kind, key, ``params``, body, computed, ``static``, ``abstract``, returnType, typeParameters, loc) ->
                      let self = Arg.arg (Python.Identifier("self"))

                      let parms = ``params`` |> List.ofArray

                      let args =
                          parms
                          |> List.choose
                              (function
                              | Pattern.Identifier (id) -> Arg.arg (Python.Identifier(id.Name)) |> Some
                              | _ -> None)

                      let varargs =
                          parms
                          |> List.choose
                              (function
                              | Pattern.RestElement (argument = argument) -> Arg.arg (Python.Identifier(argument.Name)) |> Some
                              | _ -> None)
                          |> List.tryHead

                      let arguments = Arguments.arguments (args = self :: args, ?vararg = varargs)

                      match kind with
                      | "method" ->
                          let body =
                              com.TransformAsStatements(ctx, ReturnStrategy.Return, body |> Statement.BlockStatement)

                          let name =
                              match key with
                              | Expression.Identifier (id) -> Python.Identifier(id.Name)
                              | Expression.Literal(Literal.StringLiteral(StringLiteral(value=name))) ->
                                let name = Helpers.cleanNameAsPythonIdentifier(name)
                                Python.Identifier(name)
                              | _ -> failwith $"transformAsClassDef: Unknown key: {key}"

                          FunctionDef.Create(name, arguments, body = body)
                      | "constructor" ->
                          let name = Python.Identifier("__init__")

                          let body =
                              com.TransformAsStatements(ctx, ReturnStrategy.NoReturn, body |> Statement.BlockStatement)

                          FunctionDef.Create(name, arguments, body = body)
                      | _ -> failwith $"transformAsClassDef: Unknown kind: {kind}"
                  | _ -> failwith $"transformAsClassDef: Unhandled class member {mber}" ]

        printfn $"Body length: {body.Length}: ${body}"
        let name = Helpers.cleanNameAsPythonIdentifier (id.Value.Name)

        [ yield! stmts; Statement.classDef (Python.Identifier(name), body = body, bases = bases) ]

    let transformAsFunction
        (com: IPythonCompiler)
        (ctx: Context)
        (name: Babel.Identifier)
        (parms: Babel.Pattern array)
        (body: Babel.BlockStatement)
        =
        let args =
            parms
            |> List.ofArray
            |> List.map
                (fun pattern ->
                    let name = Helpers.cleanNameAsPythonIdentifier (pattern.Name)
                    Arg.arg (Python.Identifier(name)))

        let arguments = Arguments.arguments (args = args)

        let body =
            com.TransformAsStatements(ctx, ReturnStrategy.Return, body |> Statement.BlockStatement)

        let name = Helpers.cleanNameAsPythonIdentifier (name.Name)

        FunctionDef.Create(Python.Identifier(name), arguments, body = body)

    /// Transform Babel expression as Python expression
    let rec transformAsExpr (com: IPythonCompiler) (ctx: Context) (expr: Babel.Expression): Python.Expression * list<Python.Statement> =
        printfn $"transformAsExpr: {expr}"

        match expr with
        | AssignmentExpression (left = left; operator = operator; right = right) ->
            let left, leftStmts = com.TransformAsExpr(ctx, left)
            let right, rightStmts = com.TransformAsExpr(ctx, right)

            match operator with
            | "=" ->
                Expression.namedExpr (left, right), leftStmts @ rightStmts
            | _ -> failwith $"Unsuppored assingment expression: {operator}"

        | BinaryExpression (left = left; operator = operator; right = right) ->
            let left, leftStmts = com.TransformAsExpr(ctx, left)
            let right, rightStmts = com.TransformAsExpr(ctx, right)

            let toBinOp op = Expression.binOp (left, op, right), leftStmts @ rightStmts
            let toCompare op = Expression.compare (left, [ op ], [ right ]), leftStmts @ rightStmts

            let toCall name =
                let func = Expression.name (Python.Identifier(name))
                let args = [ left; right ]
                Expression.call (func, args), leftStmts @ rightStmts

            match operator with
            | "+" -> Add |> toBinOp
            | "-" -> Sub |> toBinOp
            | "*" -> Mult |> toBinOp
            | "/" -> Div |> toBinOp
            | "%" -> Mod |> toBinOp
            | "**" -> Pow |> toBinOp
            | "<<" -> LShift |> toBinOp
            | ">>" -> RShift |> toBinOp
            | "|" -> BitOr |> toBinOp
            | "^" -> BitXor |> toBinOp
            | "&" -> BitAnd |> toBinOp
            | "==="
            | "==" -> Eq |> toCompare
            | "!=="
            | "!=" -> NotEq |> toCompare
            | ">" -> Gt |> toCompare
            | ">=" -> GtE |> toCompare
            | "<" -> Lt |> toCompare
            | "<=" -> LtE |> toCompare
            | "isinstance" -> toCall "isinstance"
            | _ -> failwith $"Unknown operator: {operator}"

        | UnaryExpression (operator = operator; argument = arg) ->
            let op =
                match operator with
                | "-" -> USub |> Some
                | "+" -> UAdd |> Some
                | "~" -> Invert |> Some
                | "!" -> Not |> Some
                | "void" -> None
                | _ -> failwith $"Unhandled unary operator: {operator}"

            let operand, stmts = com.TransformAsExpr(ctx, arg)

            match op with
            | Some op -> Expression.unaryOp (op, operand), stmts
            | _ ->
                // TODO: Should be Contant(value=None) but we cannot create that in F#
                Expression.name (id = Python.Identifier("None")), stmts

        | ArrowFunctionExpression (``params`` = parms; body = body) ->
            let args =
                parms
                |> List.ofArray
                |> List.map (fun pattern -> Arg.arg (Python.Identifier pattern.Name))

            let arguments =
                let args =
                    match args with
                    | [] -> [ Arg.arg (Python.Identifier("_"), Expression.name (Python.Identifier("None"))) ] // Need to receive unit
                    | _ -> args

                Arguments.arguments (args = args)

            let stmts = body.Body // TODO: Babel AST should be fixed. Body does not have to be a BlockStatement.

            match stmts with
            | [| Statement.ReturnStatement (argument = argument) |] ->
                let body, stmts = com.TransformAsExpr(ctx, argument)
                Expression.lambda (arguments, body), stmts
            | _ ->
                let body = com.TransformAsStatements(ctx, ReturnStrategy.Return, body)
                let name = Helpers.getIdentifier "lifted"

                let func =
                    FunctionDef.Create(name = name, args = arguments, body = body)

                Expression.name (name), [ func ]
        | CallExpression (callee = callee; arguments = args) -> // FIXME: use transformAsCall
            let func, stmts = com.TransformAsExpr(ctx, callee)

            let args, stmtArgs =
                args
                |> List.ofArray
                |> List.map (fun arg -> com.TransformAsExpr(ctx, arg))
                |> Helpers.unzipArgs

            Expression.call (func, args), stmts @ stmtArgs
        | ArrayExpression (elements = elements) ->
            let elems, stmts =
                elements
                |> List.ofArray
                |> List.map (fun ex -> com.TransformAsExpr(ctx, ex))
                |> Helpers.unzipArgs

            Expression.tuple (elems), stmts
        | Expression.Literal (Literal.NumericLiteral (value = value)) -> Expression.constant (value = value), []
        | Expression.Literal (Literal.StringLiteral (StringLiteral.StringLiteral (value = value))) ->
            Expression.constant (value = value), []
        | Expression.Identifier (Identifier (name = name)) ->
            let name = Helpers.cleanNameAsPythonIdentifier name
            Expression.name (id = Python.Identifier name), []
        | NewExpression (callee = callee; arguments = args) -> // FIXME: use transformAsCall
            let func, stmts = com.TransformAsExpr(ctx, callee)

            let args, stmtArgs =
                args
                |> List.ofArray
                |> List.map (fun arg -> com.TransformAsExpr(ctx, arg))
                |> Helpers.unzipArgs

            Expression.call (func, args), stmts @ stmtArgs
        | Expression.Super (se) -> Expression.name (Python.Identifier("super().__init__")), []
        | ObjectExpression (properties = properties) ->
            let keys, values, stmts =
                [ for prop in properties do
                      match prop with
                      | ObjectProperty (key = key; value = value) ->
                          let key, stmts1 = com.TransformAsExpr(ctx, key)
                          let value, stmts2 = com.TransformAsExpr(ctx, value)
                          key, value, stmts1 @ stmts2
                      | Babel.ObjectMethod (key = key; ``params`` = parms; body = body) ->
                          let body = com.TransformAsStatements(ctx, ReturnStrategy.Return, body)
                          let key, stmts = com.TransformAsExpr(ctx, key)

                          let args =
                              parms
                              |> List.ofArray
                              |> List.map (fun pattern -> Arg.arg (Python.Identifier pattern.Name))

                          let arguments = Arguments.arguments (args = args)
                          let name = Helpers.getIdentifier "lifted"

                          let func =
                              FunctionDef.Create(name = name, args = arguments, body = body)

                          key, Expression.name (name), stmts @ [ func ] ]
                |> List.unzip3

            Expression.dict (keys = keys, values = values), stmts |> List.collect id
        | EmitExpression (value = value; args = args) ->
            let args, stmts =
                args
                |> List.ofArray
                |> List.map (fun expr -> com.TransformAsExpr(ctx, expr))
                |> Helpers.unzipArgs

            match value with
            | "void $0" -> args.[0], stmts
            //| "raise %0" -> Raise.Create()
            | _ -> Expression.emit (value, args), stmts
        | MemberExpression (computed = true; object = object; property = Expression.Literal (literal)) ->
            let value, stmts = com.TransformAsExpr(ctx, object)
            match literal with
            | NumericLiteral (value = numeric) ->
                let attr = Expression.constant(numeric)
                Expression.subscript(value = value, slice = attr, ctx = Load), stmts
            | Literal.StringLiteral (StringLiteral (value = str)) ->
                let attr = Expression.constant (str)
                let func = Expression.name("getattr")
                Expression.call(func, args=[value; attr]), stmts
            | _ -> failwith $"transformExpr: unknown literal {literal}"
        | MemberExpression (computed = false; object = object; property = Expression.Identifier (Identifier(name = "indexOf"))) ->
            let value, stmts = com.TransformAsExpr(ctx, object)
            let attr = Python.Identifier "index"
            Expression.attribute (value = value, attr = attr, ctx = Load), stmts
        | MemberExpression (computed = false; object = object; property = Expression.Identifier (Identifier(name = "length"))) ->
            let value, stmts = com.TransformAsExpr(ctx, object)
            let func = Expression.name (Python.Identifier "len")
            Expression.call (func, [ value ]), stmts
        | MemberExpression (computed = false; object = object; property = Expression.Identifier (Identifier(name = "message"))) ->
            let value, stmts = com.TransformAsExpr(ctx, object)
            let func = Expression.name (Python.Identifier "str")
            Expression.call (func, [ value ]), stmts
        | MemberExpression (computed=true; object = object; property = property) ->
            let value, stmts = com.TransformAsExpr(ctx, object)

            let attr =
                match property with
                | Expression.Identifier (Identifier (name = name)) -> Expression.constant(name)
                | _ -> failwith $"transformAsExpr: unknown property {property}"

            let value =
                match value with
                | Name { Id = Python.Identifier (id); Context = ctx } ->
                    Expression.name (id = Python.Identifier(id), ctx = ctx)
                | _ -> value
            let func = Expression.name("getattr")
            Expression.call(func=func, args=[value; attr]), stmts
        | Expression.MemberExpression (computed=false; object = object; property = property) ->
            let value, stmts = com.TransformAsExpr(ctx, object)

            let attr =
                match property with
                | Expression.Identifier (Identifier (name = name)) -> Python.Identifier(name)
                | _ -> failwith $"transformAsExpr: unknown property {property}"

            let value =
                match value with
                | Name { Id = Python.Identifier (id)
                         Context = ctx } ->
                    // TODO: Need to make this more generic and robust
                    let id =
                        if id = "Math" then
                            //com.imports.Add("math", )
                            "math"
                        else
                            id

                    Expression.name (id = Python.Identifier(id), ctx = ctx)
                | _ -> value

            Expression.attribute (value = value, attr = attr, ctx = Load), stmts
        | Expression.Literal (Literal.BooleanLiteral (value = value)) -> Expression.constant (value = value), []
        | Expression.FunctionExpression (``params`` = parms; body = body) ->
            let args =
                parms
                |> List.ofArray
                |> List.map (fun pattern -> Arg.arg (Python.Identifier pattern.Name))

            let arguments = Arguments.arguments (args = args)

            match body.Body with
            | [| Statement.ExpressionStatement (expr) |] ->
                let body, stmts = com.TransformAsExpr(ctx, expr)
                Expression.lambda (arguments, body), stmts
            | _ ->
                let body = com.TransformAsStatements(ctx, ReturnStrategy.Return, body)

                let name = Helpers.getIdentifier "lifted"

                let func =
                    FunctionDef.Create(name = name, args = arguments, body = body)

                Expression.name (name), [ func ]
        | Expression.ConditionalExpression (test = test; consequent = consequent; alternate = alternate) ->
            let test, stmts1 = com.TransformAsExpr(ctx, test)
            let body, stmts2 = com.TransformAsExpr(ctx, consequent)
            let orElse, stmts3 = com.TransformAsExpr(ctx, alternate)

            Expression.ifExp (test, body, orElse), stmts1 @ stmts2 @ stmts3
        | Expression.Literal (Literal.NullLiteral (nl)) -> Expression.name (Python.Identifier("None")), []
        | Expression.SequenceExpression (expressions = exprs) ->
            // Sequence expressions are tricky. We currently convert them to a function that we call w/zero arguments
            let stmts =
                exprs
                |> List.ofArray
                |> List.map (fun ex -> com.TransformAsStatements(ctx, ReturnStrategy.Return, ex))
                |> List.collect id

            // let body =
            //     exprs
            //     |> List.mapi
            //         (fun i n ->
            //             if i = exprs.Length - 1 then
            //                 Statement.return' (n) // Return the last statement
            //             else
            //                 Statement.expr (n))

            let name = Helpers.getIdentifier ("lifted")

            let func =
                FunctionDef.Create(name = name, args = Arguments.arguments [], body = stmts)

            let name = Expression.name (name)
            Expression.call (name), [ func ]
        | ThisExpression (_) -> Expression.name ("self"), []
        | _ -> failwith $"transformAsExpr: Unhandled value: {expr}"

    /// Transform Babel expressions as Python statements.
    let rec transformExpressionAsStatements
        (com: IPythonCompiler)
        (ctx: Context)
        (returnStrategy: ReturnStrategy)
        (expr: Babel.Expression)
        : Python.Statement list =

        printfn $"transformExpressionAsStatements: {expr}"

        match expr with
        // Transform e.g `this["x@22"] = x;` into `setattr(self, "x@22", x)`
        | AssignmentExpression (left = MemberExpression (object = object
                                                         property = Literal (Literal.StringLiteral (StringLiteral (value = attr))))
                                right = right) ->
            // object, attr, value
            let object, stmts1 = com.TransformAsExpr(ctx, object)
            let value, stmts2 = com.TransformAsExpr(ctx, right)
            let attr = Expression.constant(attr)

            [ yield! stmts1
              yield! stmts2
              Statement.expr (value = Expression.call (func = Expression.name ("setattr"), args = [ object; attr; value ])) ]

        // Transform e.g `this.x = x;` into `self.x = x`
        | AssignmentExpression (left = left; right = right) ->
            let value, stmts = com.TransformAsExpr(ctx, right)

            let targets, stmts2: Python.Expression list * Python.Statement list =
                match left with
                | Expression.Identifier (Identifier (name = name)) ->
                    let target =
                        Python.Identifier(Helpers.cleanNameAsPythonIdentifier (name))

                    [ Expression.name (id = target, ctx = Store) ], []
                | MemberExpression (property = Expression.Identifier (id); object = object) ->
                    let attr =
                        Python.Identifier(Helpers.cleanNameAsPythonIdentifier (id.Name))

                    let value, stmts = com.TransformAsExpr(ctx, object)
                    [ Expression.attribute (value = value, attr = attr, ctx = Store) ], stmts
                | _ -> failwith $"AssignmentExpression, unknown expression: {left}"

            [ yield! stmts; yield! stmts2; Statement.assign (targets = targets, value = value) ]
        | _ ->
            // Wrap the rest in statement expression
            let expr, stmts = com.TransformAsExpr(ctx, expr)
            [ yield! stmts; Statement.expr expr ]

    /// Transform Babel statement as Python statements.
    let rec transformStatementAsStatements
        (com: IPythonCompiler)
        (ctx: Context)
        (returnStrategy: ReturnStrategy)
        (stmt: Babel.Statement)
        : Python.Statement list =
        printfn $"transformStatementAsStatements: {stmt}, returnStrategy: {returnStrategy}"

        match stmt with
        | Statement.BlockStatement (bs) ->
            [ yield! com.TransformAsStatements(ctx, returnStrategy, bs) ]
            |> transformBody returnStrategy

        | Statement.ReturnStatement (argument = arg) ->
            let expr, stmts = transformAsExpr com ctx arg

            match returnStrategy with
            | ReturnStrategy.NoReturn -> stmts @ [ Statement.expr (expr) ]
            | _ -> stmts @ [ Statement.return' (expr) ]
        | Statement.Declaration (Declaration.VariableDeclaration (VariableDeclaration (declarations = declarations))) ->
            [ for (VariableDeclarator (id = id; init = init)) in declarations do
                  let targets: Python.Expression list =
                      let name = Helpers.cleanNameAsPythonIdentifier (id.Name)
                      [ Expression.name (id = Python.Identifier(name), ctx = Store) ]

                  match init with
                  | Some value ->
                      let expr, stmts = com.TransformAsExpr(ctx, value)
                      yield! stmts
                      Statement.assign (targets, expr)
                  | None -> () ]
        | Statement.ExpressionStatement (expr = expression) ->
            // Handle Babel expressions that we need to transforme here as Python statements
            match expression with
            | Expression.AssignmentExpression (_) -> com.TransformAsStatements(ctx, returnStrategy, expression)
            | _ ->
                [ let expr, stmts = com.TransformAsExpr(ctx, expression)
                  yield! stmts
                  Statement.expr (expr) ]
        | Statement.IfStatement (test = test; consequent = consequent; alternate = alternate) ->
            let test, stmts = com.TransformAsExpr(ctx, test)

            let body =
                com.TransformAsStatements(ctx, returnStrategy, consequent)
                |> transformBody ReturnStrategy.NoReturn

            let orElse =
                match alternate with
                | Some alt ->
                    com.TransformAsStatements(ctx, returnStrategy, alt)
                    |> transformBody ReturnStrategy.NoReturn

                | _ -> []

            [ yield! stmts; Statement.if' (test = test, body = body, orelse = orElse) ]
        | Statement.WhileStatement (test = test; body = body) ->
            let expr, stmts = com.TransformAsExpr(ctx, test)

            let body =
                com.TransformAsStatements(ctx, returnStrategy, body)
                |> transformBody ReturnStrategy.NoReturn

            [ yield! stmts; Statement.while' (test = expr, body = body, orelse = []) ]
        | Statement.TryStatement (block = block; handler = handler; finalizer = finalizer) ->
            let body = com.TransformAsStatements(ctx, returnStrategy, block)

            let finalBody =
                finalizer
                |> Option.map (fun f -> com.TransformAsStatements(ctx, returnStrategy, f))

            let handlers =
                match handler with
                | Some (CatchClause (param = parm; body = body)) ->
                    let body = com.TransformAsStatements(ctx, returnStrategy, body)

                    let exn =
                        Expression.name (Python.Identifier("Exception"))
                        |> Some

                    // Insert a ex.message = str(ex) for all aliased exceptions.
                    let identifier = Python.Identifier(parm.Name)
                    // let idName = Name.Create(identifier, Load)
                    // let message = Identifier("message")
                    // let trg = Attribute.Create(idName, message, Store)
                    // let value = Call.Create(Name.Create(Identifier("str"), Load), [idName])
                    // let msg = Assign.Create([trg], value)
                    // let body =  msg :: body
                    let handlers =
                        [ ExceptHandler.exceptHandler (``type`` = exn, name = identifier, body = body) ]

                    handlers
                | _ -> []

            [ Statement.try' (body = body, handlers = handlers, ?finalBody = finalBody) ]
        | Statement.SwitchStatement (discriminant = discriminant; cases = cases) ->
            let value, stmts = com.TransformAsExpr(ctx, discriminant)

            let rec ifThenElse (fallThrough: Python.Expression option) (cases: Babel.SwitchCase list): Python.Statement list option =
                match cases with
                | [] -> None
                | SwitchCase (test = test; consequent = consequent) :: cases ->
                    let body =
                        consequent
                        |> List.ofArray
                        |> List.collect (fun x -> com.TransformAsStatements(ctx, ReturnStrategy.NoBreak, x))

                    match test with
                    | None -> body |> Some
                    | Some test ->
                        let test, st = com.TransformAsExpr(ctx, test)

                        let expr =
                            Expression.compare (left = value, ops = [ Eq ], comparators = [ test ])

                        let test =
                            match fallThrough with
                            | Some ft -> Expression.boolOp (op = Or, values = [ ft; expr ])
                            | _ -> expr
                        // Check for fallthrough
                        if body.IsEmpty then
                            ifThenElse (Some test) cases
                        else
                            [ Statement.if' (test = test, body = body, ?orelse = ifThenElse None cases) ]
                            |> Some

            let result = cases |> List.ofArray |> ifThenElse None

            match result with
            | Some ifStmt -> stmts @ ifStmt
            | None -> []
        | Statement.BreakStatement (_) -> [ Break ]
        | Statement.Declaration (Declaration.FunctionDeclaration (``params`` = parms; id = id; body = body)) ->
            [ com.TransformFunction(ctx, id, parms, body) ]
        | Statement.Declaration (Declaration.ClassDeclaration (body, id, superClass, implements, superTypeParameters, typeParameters, loc)) ->
            transformAsClassDef com ctx body id superClass implements superTypeParameters typeParameters loc
        | Statement.ForStatement (init = Some (VariableDeclaration(declarations = [| VariableDeclarator (id = id; init = Some (init)) |]))
                                  test = Some (Expression.BinaryExpression (left = left; right = right; operator = "<="))
                                  body = body) ->
            let body =
                com.TransformAsStatements(ctx, ReturnStrategy.NoReturn, body)

            let target = Expression.name (Python.Identifier id.Name)
            let start, stmts1 = com.TransformAsExpr(ctx, init)
            let stop, stmts2 = com.TransformAsExpr(ctx, right)
            let stop = Expression.binOp (stop, Add, Expression.constant (1)) // Python `range` has exclusive end.

            let iter =
                Expression.call (Expression.name (Python.Identifier "range"), args = [ start; stop ])

            stmts1
            @ stmts2
              @ [ Statement.for' (target = target, iter = iter, body = body) ]
        | Statement.LabeledStatement (body = body) -> com.TransformAsStatements(ctx, returnStrategy, body)
        | Statement.ContinueStatement (_) -> [ Continue ]
        | _ -> failwith $"transformStatementAsStatements: Unhandled: {stmt}"

    let transformBlockStatementAsStatements
        (com: IPythonCompiler)
        (ctx: Context)
        (returnStrategy: ReturnStrategy)
        (block: Babel.BlockStatement)
        : Python.Statement list =

        [ for stmt in block.Body do
              yield! transformStatementAsStatements com ctx returnStrategy stmt ]

    /// Transform Babel program to Python module.
    let transformProgram (com: IPythonCompiler) ctx (body: Babel.ModuleDeclaration array): Module =
        let returnStrategy = ReturnStrategy.Return

        let stmt: Python.Statement list =
            [ for md in body do
                  match md with
                  | Babel.ExportNamedDeclaration (decl) ->
                      match decl with
                      | Babel.VariableDeclaration (VariableDeclaration (declarations = declarations)) ->
                          for (VariableDeclarator (id, init)) in declarations do
                              let value, stmts = com.TransformAsExpr(ctx, init.Value)

                              let targets: Python.Expression list =
                                  let name = Helpers.cleanNameAsPythonIdentifier (id.Name)
                                  [ Expression.name (id = Python.Identifier(name), ctx = Store) ]

                              yield! stmts
                              yield Statement.assign (targets = targets, value = value)
                      | Babel.FunctionDeclaration (``params`` = ``params``; body = body; id = id) ->
                          yield com.TransformFunction(ctx, id, ``params``, body)

                      | Babel.ClassDeclaration (body, id, superClass, implements, superTypeParameters, typeParameters, loc) ->
                          yield! transformAsClassDef com ctx body id superClass implements superTypeParameters typeParameters loc
                      | _ -> failwith $"Unhandled Declaration: {decl}"

                  | Babel.ImportDeclaration (specifiers, source) -> yield! com.TransformAsImports(ctx, specifiers, source)
                  | Babel.PrivateModuleDeclaration (statement = statement) ->
                      yield!
                          com.TransformAsStatements(ctx, returnStrategy, statement)
                          |> transformBody returnStrategy
                  | _ -> failwith $"Unknown module declaration: {md}" ]

        let imports = com.GetAllImports()
        Module.module' (imports @ stmt)

    let getIdentForImport (ctx: Context) (moduleName: string) (name: string) =
        if String.IsNullOrEmpty name then
            None
        else
            match name with
            | "*"
            | "default" -> Path.GetFileNameWithoutExtension(moduleName)
            | _ -> name
            //|> getUniqueNameInRootScope ctx
            |> Python.Identifier
            |> Some

module Compiler =
    open Util

    type PythonCompiler (com: Compiler) =
        let onlyOnceWarnings = HashSet<string>()
        let imports = Dictionary<string, ImportFrom>()

        interface IPythonCompiler with
            member _.WarnOnlyOnce(msg, ?range) =
                if onlyOnceWarnings.Add(msg) then
                    addWarning com [] range msg

            member _.GetImportExpr(ctx, name, moduleName, r) =
                let cachedName = moduleName + "::" + name

                match imports.TryGetValue(cachedName) with
                | true, { Names = [ { AsName = localIdent } ] } ->
                    match localIdent with
                    | Some localIdent -> localIdent |> Some
                    | None -> None
                | _ ->
                    let localId = getIdentForImport ctx moduleName name

                    let nameId =
                        if name = Naming.placeholder then
                            "`importMember` must be assigned to a variable"
                            |> addError com [] r

                            (name |> Python.Identifier)
                        else
                            name |> Python.Identifier

                    let i =
                        ImportFrom.importFrom (Python.Identifier moduleName |> Some, [ Alias.alias (nameId, localId) ])

                    imports.Add(cachedName, i)

                    match localId with
                    | Some localId -> localId |> Some
                    | None -> None

            member _.GetAllImports() =
                imports.Values
                |> List.ofSeq
                |> List.map ImportFrom

            member bcom.TransformAsExpr(ctx, e) = transformAsExpr bcom ctx e
            member bcom.TransformAsStatements(ctx, ret, e) = transformExpressionAsStatements bcom ctx ret e
            member bcom.TransformAsStatements(ctx, ret, e) = transformStatementAsStatements bcom ctx ret e
            member bcom.TransformAsStatements(ctx, ret, e) = transformBlockStatementAsStatements bcom ctx ret e

            member bcom.TransformAsClassDef(ctx, body, id, superClass, implements, superTypeParameters, typeParameters, loc) =
                transformAsClassDef bcom ctx body id superClass implements superTypeParameters typeParameters loc

            member bcom.TransformFunction(ctx, name, args, body) = transformAsFunction bcom ctx name args body
            member bcom.TransformAsImports(ctx, specifiers, source) = transformAsImports bcom ctx specifiers source

        interface Compiler with
            member _.Options = com.Options
            member _.Plugins = com.Plugins
            member _.LibraryDir = com.LibraryDir
            member _.CurrentFile = com.CurrentFile
            member _.GetEntity(fullName) = com.GetEntity(fullName)
            member _.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
            member _.GetRootModule(fileName) = com.GetRootModule(fileName)
            member _.GetOrAddInlineExpr(fullName, generate) = com.GetOrAddInlineExpr(fullName, generate)
            member _.AddWatchDependency(fileName) = com.AddWatchDependency(fileName)

            member _.AddLog(msg, severity, ?range, ?fileName: string, ?tag: string) =
                com.AddLog(msg, severity, ?range = range, ?fileName = fileName, ?tag = tag)

    let makeCompiler com = PythonCompiler(com)

    let transformFile (com: Compiler) (program: Babel.Program) =
        let com = makeCompiler com :> IPythonCompiler

        let ctx =
            { DecisionTargets = []
              HoistVars = fun _ -> false
              TailCallOpportunity = None
              OptimizeTailCall = fun () -> ()
              ScopedTypeParams = Set.empty }

        let (Program body) = program
        transformProgram com ctx body