module MicroCosmo.IR.ILBuilder

open System.Collections.Generic
open MicroCosmo.SemanticAnalysis
open MicroCosmo.SemanticAnalysis.SemanticAnalysisResult
open MicroCosmo.IL
open MicroCosmo

type private ILVariableScope =
    | FieldScope of ILVariable
    | ArgumentScope of int16
    | LocalScope of int16

type private VariableMappingDictionary = Dictionary<Ast.VariableDeclarationStatement, ILVariableScope>

module private ILBuilderUtilities =
    let typeOf =
        function
        | Ast.NoneType  -> typeof<System.Void>
        | Ast.Bool      -> typeof<bool>
        | Ast.Int       -> typeof<int>
        | Ast.Double    -> typeof<float>
        | Ast.String    -> typeof<string>
        | Ast.Any       -> typeof<System.Object>

    let createILVariable decl = function
        Ast.VariableDeclarationStatement(i, t, e, a) as d ->
            {
                ILVariable.Type = if a then (typeOf t).MakeArrayType() else typeOf t; 
                Name = i;
            }

open ILBuilderUtilities

type ILMethodBuilder(semanticAnalysisResult : SemanticAnalysisResult,
                     variableMappings : VariableMappingDictionary) =
    let mutable argumentIndex = 0s
    let mutable localIndex = 0s
    let arrayAssignmentLocals = Dictionary<Ast.Expression, int16>()
    let mutable labelIndex = 0
    let currentWhileStatementEndLabel = Stack<ILLabel>()

    let lookupILVariableScope identifierRef =
        let declaration = semanticAnalysisResult.SymbolTable.[identifierRef]
        variableMappings.[declaration]

    let makeLabel() =
        let result = labelIndex
        labelIndex <- labelIndex + 1
        result

    let rec processBinaryExpression =
        function
        | (l, Ast.Or, r) ->
            let leftIsFalseLabel = makeLabel()
            let endLabel = makeLabel()
            List.concat [ processExpression l
                          [ ILOpCode.Brfalse leftIsFalseLabel ]
                          [ ILOpCode.Ldc_I4 1 ]
                          [ ILOpCode.Br endLabel ]
                          [ ILOpCode.Label leftIsFalseLabel ]
                          processExpression r
                          [ ILOpCode.Label endLabel ] ]
        | (l, Ast.And, r) ->
            let leftIsTrueLabel = makeLabel()
            let endLabel = makeLabel()
            List.concat [ processExpression l
                          [ ILOpCode.Brtrue leftIsTrueLabel ]
                          [ ILOpCode.Ldc_I4 0 ]
                          [ ILOpCode.Br endLabel ]
                          [ ILOpCode.Label leftIsTrueLabel ]
                          processExpression r
                          [ ILOpCode.Label endLabel ] ]
        | (l, op, r) -> List.concat [ (processExpression l);
                                      (processExpression r);
                                      [ processBinaryOperator op ] ]

    and processBinaryOperator =
        function
        | Ast.Sum   -> ILOpCode.Add
        | Ast.Div   -> ILOpCode.Div
        | Ast.Mult  -> ILOpCode.Mul
        | Ast.Mod   -> ILOpCode.Rem
        | Ast.Diff  -> ILOpCode.Sub
        | Ast.Eq    -> ILOpCode.Ceq
        | Ast.Gt    -> ILOpCode.Cgt
        | Ast.GtEq  -> ILOpCode.Cge
        | Ast.Lt    -> ILOpCode.Clt
        | Ast.LtEq  -> ILOpCode.Cle
        | o         -> failwith (sprintf "Unsupported binary operator: %A" o)

    and processIdentifierLoad identifierRef =
        match lookupILVariableScope identifierRef with
        | ILVariableScope.FieldScope(v)    -> [ ILOpCode.Ldsfld v ]
        | ILVariableScope.ArgumentScope(i) -> [ ILOpCode.Ldarg i ]
        | ILVariableScope.LocalScope(i)    -> [ ILOpCode.Ldloc i ]

    and processIdentifierStore identifierRef =
        match lookupILVariableScope identifierRef with
        | ILVariableScope.FieldScope(v)    -> [ ILOpCode.Stsfld v ]
        | ILVariableScope.ArgumentScope(i) -> [ ILOpCode.Starg i ]
        | ILVariableScope.LocalScope(i)    -> [ ILOpCode.Stloc i ]

    and processExpression expression =
        match expression with
        | Ast.VariableAssignmentExpression(i, e) ->
            List.concat [ processExpression e
                          [ ILOpCode.Dup ]
                          processIdentifierStore i ]
        | Ast.ArrayVariableAssignmentExpression(i, e1, e2) as ae ->
            List.concat [ processIdentifierLoad i
                          processExpression e1
                          processExpression e2
                          [ ILOpCode.Dup ]
                          [ ILOpCode.Stloc arrayAssignmentLocals.[ae] ]
                          [ ILOpCode.Stelem (typeOf (semanticAnalysisResult.SymbolTable.GetIdentifierTypeSpec i).Type) ]
                          [ ILOpCode.Ldloc arrayAssignmentLocals.[ae] ] ]
        | Ast.BinaryExpression(a, b, c) -> processBinaryExpression (a, b, c)
        | Ast.UnaryExpression(op, e) ->
            List.concat [ processExpression e
                          processUnaryOperator op]
        | Ast.IdentifierExpression(i) -> processIdentifierLoad i
        | Ast.ArrayIdentifierExpression(i, e) ->
            List.concat [ processIdentifierLoad i
                          processExpression e
                          [ ILOpCode.Ldelem (typeOf (semanticAnalysisResult.SymbolTable.GetIdentifierTypeSpec i).Type) ] ]
        | Ast.FunctionCallExpression(i, a) ->
            List.concat [ a |> List.collect processExpression
                          [ ILOpCode.Call i ] ]
        | Ast.ArraySizeExpression(i) ->
            List.concat [ processIdentifierLoad i
                          [ ILOpCode.Ldlen ] ]
        | Ast.LiteralExpression(l) ->
            match l with
            | Ast.IntLiteral(x)     -> [ ILOpCode.Ldc_I4 x ]
            | Ast.DoubleLiteral(x)  -> [ ILOpCode.Ldc_R8 x ]
            | Ast.BoolLiteral(x)    -> [ (if x then ILOpCode.Ldc_I4(1) else ILOpCode.Ldc_I4 0) ]
        | Ast.ArrayAllocationExpression(t, e) ->
            List.concat [ processExpression e
                          [ ILOpCode.Newarr (typeOf t) ] ]

    and processUnaryOperator =
        function
        | Ast.Not   -> [ ILOpCode.Ldc_I4 0; ILOpCode.Ceq ]
        | Ast.Minus -> [ ILOpCode.Neg ]
        | Ast.Plus  -> [ ]

    and processStatement =
        function
        | Ast.ExpressionStatement(x) ->
            match x with
            | Ast.Empty -> []
            | x ->
                let isNotVoid = semanticAnalysisResult.ExpressionTypes.[x].Type <> Ast.NoneType
                List.concat [ processExpression x
                              (if isNotVoid then [ ILOpCode.Pop ] else []) ]
                
        | Ast.BlockStatement(s) -> s |> List.collect processStatement
        | Ast.IfStatement(e, s1, Some(s2)) ->
            let thenLabel = makeLabel()
            let endLabel = makeLabel()
            List.concat [ processExpression e
                          [ ILOpCode.Brtrue thenLabel ]
                          processStatement s2
                          [ ILOpCode.Br endLabel ]
                          [ ILOpCode.Label thenLabel ]
                          processStatement s1
                          [ ILOpCode.Label endLabel ] ]
        | Ast.IfStatement(e, s1, None) ->
            let thenLabel = makeLabel()
            let endLabel = makeLabel()
            List.concat [ processExpression e
                          [ ILOpCode.Brtrue thenLabel ]
                          [ ILOpCode.Br endLabel ]
                          [ ILOpCode.Label thenLabel ]
                          processStatement s1
                          [ ILOpCode.Label endLabel ] ]
        | Ast.WhileStatement(e, s) ->
            let startLabel = makeLabel()
            let conditionLabel = makeLabel()
            let endLabel = makeLabel()
            currentWhileStatementEndLabel.Push endLabel
            let result = List.concat [ [ ILOpCode.Br conditionLabel ]
                                       [ ILOpCode.Label startLabel ]
                                       processStatement s
                                       [ ILOpCode.Label conditionLabel ]
                                       processExpression e
                                       [ ILOpCode.Brtrue startLabel ]
                                       [ ILOpCode.Label endLabel ] ]
            currentWhileStatementEndLabel.Pop() |> ignore
            result
        | Ast.ReturnStatement(x) ->
            match x with
            | Some(x) -> (processExpression x) @ [ ILOpCode.Ret ]
            | None    -> [ ILOpCode.Ret ]
        | Ast.BreakStatement -> [ ILOpCode.Br (currentWhileStatementEndLabel.Peek()) ]

    let processVariableDeclaration (mutableIndex : byref<_>) f d =
        let v = createILVariable d
        variableMappings.Add(d, f mutableIndex)
        mutableIndex <- mutableIndex + 1s
        v

    let processLocalDeclaration declaration =
        processVariableDeclaration &localIndex (fun i -> ILVariableScope.LocalScope(i)) declaration
        
    let processParameter declaration =
        processVariableDeclaration &argumentIndex (fun i -> ILVariableScope.ArgumentScope(i)) declaration

    let rec collectLocalDeclarations statement =
        let rec fromStatement =
            function
            | Ast.ExpressionStatement(es) ->
                match es with
                | Ast.Empty -> []
                | e -> fromExpression e
                
            | Ast.BlockStatement(statements) ->
                List.concat [ statements |> List.collect collectLocalDeclarations ]
                
            | Ast.IfStatement(e, s1, Some(s2)) ->
                List.concat [ fromExpression e
                              collectLocalDeclarations s1
                              collectLocalDeclarations s2 ]
                              
            | Ast.IfStatement(e, s1, None) ->
                List.concat [ fromExpression e
                              collectLocalDeclarations s1 ]
                              
            | Ast.WhileStatement(e, s) ->
                List.concat [ fromExpression e
                              collectLocalDeclarations s ]
                              
            | Ast.ReturnStatement(Some(e)) ->
                List.concat [ fromExpression e ]
                
            | _ -> []

        and fromExpression =
            function
            | Ast.VariableAssignmentExpression(i, e) -> fromExpression e
            | Ast.ArrayVariableAssignmentExpression(i, e1, e2) as ae ->
                let v = {
                    ILVariable.Type = typeOf ((semanticAnalysisResult.SymbolTable.GetIdentifierTypeSpec i).Type); 
                    Name = "ArrayAssignmentTemp" + string localIndex;
                }
                arrayAssignmentLocals.Add(ae, localIndex);
                localIndex <- localIndex + 1s
                List.concat [ [ v ]; fromExpression e2 ]
                
            | Ast.BinaryExpression(l, op, r)      -> List.concat [ fromExpression l; fromExpression r; ]
            | Ast.UnaryExpression(op, e)          -> fromExpression e
            | Ast.ArrayIdentifierExpression(i, e) -> fromExpression e
            | Ast.FunctionCallExpression(i, a)    -> a |> List.collect fromExpression
            | Ast.ArrayAllocationExpression(t, e) -> fromExpression e
            | _ -> []

        fromStatement statement

    member x.BuildMethod(returnType, name, parameters, (localDeclarations, statements)) =
        {
            Name       = name;
            ReturnType = typeOf returnType;
            Parameters = parameters |> List.map processParameter;
            Locals     = List.concat [ localDeclarations |> List.map processLocalDeclaration;
                                       statements |> List.collect collectLocalDeclarations ]
            Body       = statements |> List.collect processStatement;
        }

type ILBuilder(semanticAnalysisResult) =
    let variableMappings = new VariableMappingDictionary(HashIdentity.Reference)

    let processStaticVariableDeclaration d =
        let v = createILVariable d
        variableMappings.Add(d, ILVariableScope.FieldScope(v))
        v

    member x.BuildClass (program : Ast.Program) =
        let variableDeclarations =
            program
            |> List.choose (fun x ->
                match x with
                | Ast.VariableDeclarationStatement(x) -> Some(x)
                | _ -> None)
    
        let functionDeclarations =
            program
            |> List.choose (fun x ->
                match x with
                | Ast.FunctionDeclarationStatement(_, _, _, _ as a) -> Some a
                | _ -> None)

        let processFunctionDeclaration functionDeclaration =
            let ilMethodBuilder = new ILMethodBuilder(semanticAnalysisResult, variableMappings)
            ilMethodBuilder.BuildMethod functionDeclaration

        let builtInMethods = [
            {
                Name = "readint";
                ReturnType = typeof<int>;
                Parameters = [];
                Locals = [];
                Body = [ CallClr(typeof<System.Console>.GetMethod("ReadLine"))
                         CallClr(typeof<System.Convert>.GetMethod("ToInt32", [| typeof<string> |]))
                         Ret ];
            };
            {
                Name = "readreal";
                ReturnType = typeof<float>;
                Parameters = [];
                Locals = [];
                Body = [ CallClr(typeof<System.Console>.GetMethod("ReadLine"))
                         CallClr(typeof<System.Convert>.GetMethod("ToDouble", [| typeof<string> |]))
                         Ret ];
            };
            {
                Name = "println";
                ReturnType = typeof<System.Void>;
                Parameters = [ { Type = typeof<System.Object>; Name = "value"; }];
                Locals = [];
                Body = [ Ldarg(0s)
                         CallClr(typeof<System.Console>.GetMethod("WriteLine", [| typeof<System.Object> |]))
                         Ret ];
            };
            {
                Name = "print";
                ReturnType = typeof<System.Void>;
                Parameters = [ { Type = typeof<float>; Name = "value"; }];
                Locals = [];
                Body = [ Ldarg(0s)
                         CallClr(typeof<System.Console>.GetMethod("Write", [| typeof<System.Object> |]))
                         Ret ];
            } ]

        {
            Fields  = variableDeclarations |> List.map processStaticVariableDeclaration;
            Methods = List.concat [ builtInMethods
                                    functionDeclarations |> List.map processFunctionDeclaration ];
        }