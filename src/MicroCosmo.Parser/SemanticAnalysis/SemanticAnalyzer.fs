module MicroCosmo.SemanticAnalyzer

open MicroCosmo.SemanticAnalysis.SemanticAnalysisResult
open MicroCosmo.SemanticAnalysis.SymbolTable
open MicroCosmo.SemanticAnalysis.FunctionTable
open MicroCosmo.CompilerErrors
open MicroCosmo.ExpressionTypeTable

open System
open System.Collections.Generic

let analyze program =

    try
        let symbolTable   = new SymbolTable(program)
        let functionTable = new FunctionTable(program)
        
        if not (functionTable.ContainsKey "main") then
            raise (missingEntryPoint())
        
        let main = functionTable.["main"]
        if (main.ParameterTypes <> []) then 
            raise (missingEntryPoint())
        
        let expressionTypes = new ExpressionTypeTable(program, functionTable, symbolTable)
        
        Result.Ok {
            SymbolTable     = symbolTable;
            ExpressionTypes = expressionTypes;
        }
        
    with _ as ex  -> Result.Error ex