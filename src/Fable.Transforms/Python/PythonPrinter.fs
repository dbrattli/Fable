module Fable.Transforms.PythonPrinter

open System
open Fable
open Fable.AST
open Fable.AST.Python

type SourceMapGenerator =
    abstract AddMapping:
        originalLine: int
        * originalColumn: int
        * generatedLine: int
        * generatedColumn: int
        * ?name: string
        -> unit

type Writer =
    inherit IDisposable
    abstract MakeImportPath: string -> string
    abstract Write: string -> Async<unit>

type PrinterImpl(writer: Writer, map: SourceMapGenerator) =
    // TODO: We can make this configurable later
    let indentSpaces = "    "
    let builder = Text.StringBuilder()
    let mutable indent = 0
    let mutable line = 1
    let mutable column = 0

    let addLoc (loc: SourceLocation option) =
        match loc with
        | None -> ()
        | Some loc ->
            map.AddMapping(originalLine = loc.start.line,
                           originalColumn = loc.start.column,
                           generatedLine = line,
                           generatedColumn = column,
                           ?name = loc.identifierName)

    member _.Flush(): Async<unit> =
        async {
            do! writer.Write(builder.ToString())
            builder.Clear() |> ignore
        }

    member _.PrintNewLine() =
        builder.AppendLine() |> ignore
        line <- line + 1
        column <- 0

    interface IDisposable with
        member _.Dispose() = writer.Dispose()

    interface Printer with
        member _.Line = line
        member _.Column = column

        member _.PushIndentation() =
            indent <- indent + 1

        member _.PopIndentation() =
            if indent > 0 then indent <- indent - 1

        member _.AddLocation(loc) =
            addLoc loc

        member _.Print(str, loc) =
            addLoc loc

            if column = 0 then
                let indent = String.replicate indent indentSpaces
                builder.Append(indent) |> ignore
                column <- indent.Length

            builder.Append(str) |> ignore
            column <- column + str.Length

        member this.PrintNewLine() =
            this.PrintNewLine()

        member this.MakeImportPath(path) =
            writer.MakeImportPath(path)

let run writer map (program: Module): Async<unit> =

    let printDeclWithExtraLine extraLine (printer: Printer) (decl: Statement) =
        (decl :> IPrint).Print(printer)

        if printer.Column > 0 then
            //printer.Print(";")
            printer.PrintNewLine()
        if extraLine then
            printer.PrintNewLine()

    async {
        use printer = new PrinterImpl(writer, map)

        let imports, restDecls =
            program.Body |> List.splitWhile (function
                | Import _
                | ImportFrom _ -> true
                | _ -> false)

        for decl in imports do
            printDeclWithExtraLine false printer decl

        printer.PrintNewLine()
        do! printer.Flush()

        for decl in restDecls do
            printDeclWithExtraLine true printer decl
            // TODO: Only flush every XXX lines?
            do! printer.Flush()
    }
// If the queryItemsHandler succeeds, the queryItems will be used in a request to CDF, and the resulting aggregates will be wrapped in an OK result-type.