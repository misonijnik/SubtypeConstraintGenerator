namespace VSharp.Core

open VSharp

module internal Merging =

    type private MergeType =
        | StructMerge of termType
        | ArrayMerge of termType
        | BoolMerge
        | DefaultMerge

    let private mergeTypeOf term =
        match term.term with
        | Struct (_, t) -> StructMerge t
        | Array (_, _, _, _, _, _, t) -> ArrayMerge t
        | _ when isBool term -> BoolMerge
        | _ -> DefaultMerge

    let guardOf term =
        match term.term with
        | GuardedValues(gs, _) -> disjunction term.metadata gs
        | _ -> makeTrue term.metadata

    let private readHeap = State.readHeap Metadata.empty
    let private readStatics = State.readStatics Metadata.empty
    let private readTerm = State.readTerm Metadata.empty

    let rec private boolMerge = function
        | [] -> []
        | [(g, v)] as gvs ->
            match g with
            | True -> gvs
            | _ -> [propagateGuard g v]
        | [(g1, v1); (g2, v2)] -> [(g1 ||| g2, (g1 &&& v1) ||| (g2 &&& v2))]
        | (g, v)::gvs ->
            let guard = List.fold (|||) g (List.map fst gvs)
            let value = List.fold (fun acc (g, v) -> acc ||| (g &&& v)) (g &&& v) gvs
            [(guard, value)]

    and keysResolver<'a, 'b, 'c when 'c : equality> r (read : bool -> 'a -> 'b -> termType -> term memoryCell) keyMapper getter (k : heapKey<'c, fql>) hgvs =
        let key = keyMapper k
        let typ = Option.get k.FQL |> typeOfFQL
        let instIfShould = function
            | _, g, Some v -> (g, v)
            | i, g, _ -> (g, read r (getter i) key typ)
        mergeCells <| List.map instIfShould hgvs

    and keysResolver2<'a, 'b, 'c, 'd when 'c : equality> r h1 h2 (read : bool -> 'a -> 'b -> termType -> term memoryCell) keyMapper resolve (k : heapKey<'c, fql>) v1 v2 : 'd =
        let read h =
            let key = keyMapper k
            let typ = Option.get k.FQL |> typeOfFQL
            read r h key typ
        match v1, v2 with
        | Some v1, Some v2 -> resolve v1 v2
        | Some v, _ -> resolve v (read h2)
        | _, Some v -> resolve (read h1) v
        | _, _ -> __unreachable__()

    and private structMerge = function
        | [] -> []
        | [(g, v)] as gvs ->
            match g with
            | True -> gvs
            | _ -> [propagateGuard g v]
        | (x :: _) as gvs ->
            let t = x |> snd |> typeOf
            assert(gvs |> Seq.map (snd >> typeOf) |> Seq.forall ((=) t))
            let gs, vs = List.unzip gvs
            let extractFields = term >> function
                | Struct(fs, _) -> fs
                | t -> internalfailf "Expected struct, got %O" t
            let fss = vs |> List.map extractFields
            let getter i = {value = List.item i vs; created = Timestamp.zero; modified = Timestamp.zero}
            let merged = keysResolver<term memoryCell, fql, string> false readTerm getFQLOfKey getter |> Heap.merge gs fss
            [(Propositional.disjunction Metadata.empty gs, Struct (fst x).metadata merged t)]

    and private arrayMerge = function
        | [] -> []
        | [(g, v)] as gvs ->
            match g with
            | True -> gvs
            | _ -> [propagateGuard g v]
        | (x :: _) as gvs ->
            let t = x |> snd |> typeOf
            assert(gvs |> Seq.map (snd >> typeOf) |> Seq.forall ((=) t))
            let gs, vs = List.unzip gvs
            let extractArrayInfo = term >> function
                | Array(dim, len, lower, init, contents, lengths, _) -> (dim, len, lower, init, contents, lengths)
                | t -> internalfailf "Expected array, got %O" t
            let ds, lens, lows, inits, contents, lengths =
                vs
                |> Seq.map extractArrayInfo
                |> fun info -> Seq.foldBack (fun (d, l, lw, i, c, ls) (da, la, lwa, ia, ca, lsa) -> (d::da, l::la, lw::lwa, i::ia, c::ca, ls::lsa)) info ([], [], [], [], [], [])
            let d = List.unique ds
            let l = merge <| List.zip gs lens
            let getter i = {value = List.item i vs; created = Timestamp.zero; modified = Timestamp.zero}
            let resolveKeys = keysResolver<term memoryCell, fql, term> false readTerm getFQLOfKey getter
            let mergedLower =  Heap.merge gs lows resolveKeys
            let mergedContents = Heap.merge gs contents resolveKeys
            let mergedLengths = Heap.merge gs lengths resolveKeys
            let mergedInit = inits |> Seq.map2 (fun ng init -> Seq.map (fun (g, v) -> (ng &&& g, v)) init) gs |> Seq.concat |> List.ofSeq |> mergeSame
            [(Propositional.disjunction Metadata.empty gs, Array Metadata.empty d l mergedLower mergedInit mergedContents mergedLengths t)]

    and private simplify (|Unguard|_|) gvs =
        let rec loop gvs out =
            match gvs with
            | [] -> out
            | ((True, _) as gv)::_ -> [gv]
            | (False, _)::gvs' -> loop gvs' out
            | (g, Unguard us)::gvs' ->
                let guarded = us |> List.map (fun (g', v) -> (g &&& g', v))
                loop gvs' (List.append (simplify (|Unguard|_|) guarded) out)
            | gv::gvs' -> loop gvs' (gv::out)
        loop gvs []

    and mergeSame<'a when 'a : equality> : (term * 'a) list -> (term * 'a) list = function
        | [] -> []
        | [_] as xs -> xs
        | [(g1, v1); (g2, v2)] as gvs -> if v1 = v2 then [(g1 ||| g2, v1)] else gvs
        | gvs ->
            let rec loop gvs out =
                match gvs with
                | [] -> out
                | (g, v)::gvs' ->
                    let eq, rest = List.partition (snd >> (=) v) gvs'
                    let joined = List.fold (|||) g (List.map fst eq)
                    match joined with
                    | True -> [(joined, v)]
                    | False -> loop rest out
                    | _ -> loop rest ((joined, v)::out)
            loop gvs []

    and private typedMerge gvs t =
        match t with
        | BoolMerge -> boolMerge gvs
        | StructMerge _ -> structMerge gvs
        | ArrayMerge _ -> arrayMerge gvs
        | DefaultMerge -> gvs

    and propagateGuard g v =
        match v.term with
        | Struct(contents, t) ->
            let contents' = Heap.map' (fun _ cell -> { cell with value = merge [(g, cell.value)] }) contents
            (Terms.True, Struct v.metadata contents' t)
        | Array(dimension, len, lower, init, contents, lengths, t) ->
            let contents' = Heap.map' (fun _ cell -> { cell with value = merge [(g, cell.value)] }) contents
            let lower' = Heap.map' (fun _ cell -> { cell with value = merge [(g, cell.value)] }) lower
            let lengths' = Heap.map' (fun _ cell -> { cell with value = merge [(g, cell.value)] }) lengths
            let init' = List.map (fun (gi, i) -> gi &&& g, i) init
            (Terms.True, Array v.metadata dimension len lower' init' contents' lengths' t)
        | _ -> (g, v)

    and private compress = function
        | [] -> []
        | [(_, v)] -> [True, v]
        | [(_, v1); (_, v2)] as gvs when mergeTypeOf v1 = mergeTypeOf v2 -> typedMerge (mergeSame gvs) (mergeTypeOf v1)
        | [_; _] as gvs -> gvs
        | gvs ->
            gvs
            |> mergeSame
            |> List.groupBy (snd >> mergeTypeOf)
            |> List.collect (fun (t, gvs) -> if List.length gvs >= 2 then typedMerge gvs t else gvs)

    and merge gvs =
        match compress (simplify (|UnionT|_|) gvs) with
        | [(True, v)] -> v
        | [(g, v)] when Terms.isBool v -> g &&& v
        | gvs' -> Union Metadata.empty gvs'

    and mergeCells (gcs : list<term * term memoryCell>) : term memoryCell =
        let foldCell (acc1, acc2, acc3) (g, cell) = ((g, cell.value)::acc1, min acc2 cell.created, max acc3 cell.modified)
        let gvs, c, m = gcs |> List.fold foldCell ([], System.UInt32.MaxValue, System.UInt32.MinValue)
        { value = merge gvs; created = c; modified = m }

    and merge2Terms g h u v =
        let g = guardOf u &&& g
        let h = guardOf v &&& h
        match g, h with
        | _, _ when u = v -> u
        | True, _
        | _, False -> u
        | False, _
        | _, True -> v
        | ErrorT _, _ -> g
        | _, ErrorT _ -> h
        | _ -> merge [(g, u); (h, v)]

    and merge2Cells g h ({value = u;created = cu;modified = mu} as ucell : term memoryCell) ({value = v;created = cv;modified = mv} as vcell : term memoryCell) =
        let g = guardOf u &&& g
        let h = guardOf v &&& h
        match g, h with
        | _, _ when u = v -> { value = u; created = min cu cv; modified = min mu mv }
        | True, _
        | _, False -> ucell
        | False, _
        | _, True -> vcell
        | ErrorT _, _ -> { ucell with value = g }
        | _, ErrorT _ -> { vcell with value = h }
        | _ -> mergeCells [(g, ucell); (h, vcell)]

    and mergeDefinedHeaps restricted read guards heaps =
        let getter i = List.item i heaps
        Heap.merge guards heaps (keysResolver restricted read Heap.getKey getter)

    and mergeGeneralizedHeaps<'a when 'a : equality> read guards (heaps : list<'a generalizedHeap>) =
        let (|MergedHeap|_|) = function Merged gvs -> Some gvs | _ -> None
        let guardsAndHeaps = List.zip guards heaps |> simplify (|MergedHeap|_|)
        let Merged = function
            | [(True, x)] -> x
            | xs -> Merged xs
        // TODO: non-restricted heaps should be merged in a different way
        let defined, undefined =
            guardsAndHeaps
            |> List.mappedPartition (function (g, Defined(r, s)) -> Some(g, r, s) | _ -> None)
        if defined.IsEmpty then
            undefined |> mergeSame |> Merged
        else
            let definedGuards, restricted, definedHeaps = List.unzip3 defined
            let restricted = List.unique restricted
            let definedHeap = mergeDefinedHeaps restricted read definedGuards definedHeaps |> State.Defined restricted
            if undefined.IsEmpty then definedHeap
            else
                let definedGuard = disjunction Metadata.empty definedGuards
                (definedGuard, definedHeap)::undefined |> mergeSame |> Merged

    and private merge2GeneralizedHeaps g1 g2 h1 h2 read resolve =
        match h1, h2 with
        | Defined(r1, h1), Defined(r2, h2) ->
            assert(r1 = r2)
            Heap.merge2 h1 h2 (keysResolver2 r1 h1 h2 read Heap.getKey resolve) |> State.Defined r1
        | _ -> mergeGeneralizedHeaps read [g1; g2] [h1; h2]

    and merge2States condition1 condition2 (state1 : state) (state2 : state) =
        match condition1, condition2 with
        | True, _ -> state1
        | False, _ -> state2
        | _, True -> state2
        | _, False -> state1
        | _ ->
            assert(state1.pc = state2.pc)
            assert(state1.frames = state2.frames)
            let resolve = merge2Cells condition1 condition2
            let mergedStack = Utils.MappedStack.merge2 state1.stack state2.stack resolve (State.stackLazyInstantiator state1)
            let mergedHeap = merge2GeneralizedHeaps condition1 condition2 state1.heap state2.heap readHeap resolve
            let mergedStatics = merge2GeneralizedHeaps condition1 condition2 state1.statics state2.statics readStatics resolve
            { state1 with stack = mergedStack; heap = mergedHeap; statics = mergedStatics; traceConstraint = List.append state1.traceConstraint state2.traceConstraint |> Seq.distinct |> List.ofSeq }

    and mergeStates conditions states : state =
        assert(List.length states > 0)
        let first : state = List.head states
        let frames = first.frames
        let path = first.pc
        let constr = states |> List.map (fun s -> s.traceConstraint) |> List.concat
        let tv = first.typeVariables
        assert(states |> List.forall (fun s -> s.frames = frames))
        assert(states |> List.forall (fun s -> s.pc = path))
        assert(states |> List.forall (fun s -> s.typeVariables = tv))
        let mergedStack = Utils.MappedStack.merge conditions (List.map State.stackOf states) mergeCells (State.stackLazyInstantiator first)
        let mergedHeap = mergeGeneralizedHeaps readHeap conditions (List.map State.heapOf states)
        let mergedStatics = mergeGeneralizedHeaps readStatics conditions (List.map State.staticsOf states)
        { stack = mergedStack; heap = mergedHeap; statics = mergedStatics; frames = frames; pc = path; traceConstraint = constr; typeVariables = tv }

    and genericSimplify gvs =
        let rec loop gvs out =
            match gvs with
            | [] -> out
            | ((True, _) as gv)::_ -> [gv]
            | (False, _)::gvs' -> loop gvs' out
            | gv::gvs' -> loop gvs' (gv::out)
        loop gvs [] |> mergeSame

// ------------------------------------ Mapping non-term sequences ------------------------------------

    and guardedMapWithoutMerge f gvs =
        List.map (fun (g, v) -> (g, f v)) gvs

    and commonGuardedMapk mapper gvs merge k =
        let foldFunc gvs (g, v) k =
            mapper v (fun v' -> k ((g, v') :: gvs))
        Cps.List.foldlk foldFunc [] gvs (merge >> k)
    and guardedMap mapper gvs = commonGuardedMapk (Cps.ret mapper) gvs merge id

// ---------------------- Applying functions to terms and mapping term sequences ----------------------

    let commonGuardedErroredMapk mapper errorMapper gvs merge k =
        let foldFunc gvs (g, v) k =
            if isError v then k ((g, errorMapper v) :: gvs)
            else mapper v (fun t -> k ((g, t) :: gvs))
        Cps.List.foldlk foldFunc [] gvs (merge >> k)

    let commonGuardedErroredApplyk f errorHandler term merge k =
        match term.term with
        | Error _ -> errorHandler term |> k
        | Union gvs -> commonGuardedErroredMapk f errorHandler gvs merge k
        | _ -> f term k
    let commonGuardedErroredApply f errorHandler term merge = commonGuardedErroredApplyk (Cps.ret f) errorHandler term merge id
    let guardedErroredApply f term = commonGuardedErroredApply f id term merge

    let commonGuardedErroredStatedMapk mapper errorMapper gvs state merge k =
        let foldFunc (gvs, egs, vgs, states) (g, v) k =
            if isError v then k ((g, errorMapper v) :: gvs, g :: egs, vgs, states)
            // TODO: do not map if (guard & pc) = false
            else mapper (State.withPathCondition state g) v (fun (t, s) -> k ((g, t) :: gvs, egs, g :: vgs, State.popPathCondition s :: states))
        Cps.List.foldlk foldFunc ([], [], [], []) gvs (fun (gvs, egs, vgs, states) ->
        let eg = disjunction Metadata.empty egs
        let state' = mergeStates (eg :: vgs) (state :: states)
        k (merge gvs, state'))

    let commonGuardedErroredStatedApplyk f errorHandler state term merge k =
        match term.term with
        | Error _ -> k (errorHandler term, state)
        | Union gvs -> commonGuardedErroredStatedMapk f errorHandler gvs state merge k
        | _ -> f state term k
    let guardedErroredStatedApplyk f state term k = commonGuardedErroredStatedApplyk f id state term merge k
    let guardedErroredStatedApply f state term = guardedErroredStatedApplyk (Cps.ret2 f) state term id

// ----------------------------------------------------------------------------------------------------

    let unguard = function
        | {term = Union gvs} -> gvs
        | t -> [(True, t)]

    let erroredUnguard term =
        let ges, gvs = term |> unguard |> List.partition (snd >> isError)
        ges, merge gvs

    let productUnion f t1 t2 =
        match t1.term, t2.term with
        | Union gvs1, Union gvs2 ->
            gvs1 |> List.collect (fun (g1, v1) ->
            gvs2 |> List.map (fun (g2, v2) ->
            (g1 &&& g2, f v1 v2)))
            |> merge
        | Union gvs1, _ ->
            gvs1 |> List.map (fun (g1, v1) -> (g1, f v1 t2)) |> merge
        | _, Union gvs2 ->
            gvs2 |> List.map (fun (g2, v2) -> (g2, f t1 v2)) |> merge
        | _ -> f t1 t2

    let rec private genericGuardedCartesianProductRec mapper ctor gacc xsacc = function
        | x::xs ->
            mapper x
            |> List.collect (fun (g, v) ->
                genericGuardedCartesianProductRec mapper ctor (gacc &&& g) (List.append xsacc [v]) xs)
            |> genericSimplify
        | [] -> [(gacc, ctor xsacc)]
    let genericGuardedCartesianProduct mapper ctor xs =
        genericGuardedCartesianProductRec mapper ctor True [] xs

    let rec private guardedCartesianProductRec mapper ctor gacc xsacc = function
        | x::xs ->
            mapper x
            |> List.collect (fun (g, v) ->
                let g' = gacc &&& g
                if isError v then [(g', v)]
                else
                    guardedCartesianProductRec mapper ctor g' (List.append xsacc [v]) xs)
            |> genericSimplify
        | [] -> [(gacc, ctor xsacc)]

    let guardedCartesianProduct mapper ctor terms =
        guardedCartesianProductRec mapper ctor True [] terms

    let rec private guardedSequentialProductRec gacc terms k =
        match terms with
        | x::xs ->
            let errorsOfX, x' = List.partition (snd >> isError) (unguard x)
            let errorsOfX = List.map (mapfst ((&&&) gacc)) errorsOfX
            match x' with
            | [] -> k errorsOfX None False
            | _ ->
                let gacc' = gacc &&& disjunction Metadata.empty (List.map fst x')
                guardedSequentialProductRec gacc' xs (fun errors results ->
                let results =
                    match results with
                    | Some results ->
                        let x' = List.map (mapfst ((&&&) gacc)) x'
                        Some (x'::results)
                    | None -> None
                k (errorsOfX @ errors) results)
        | [] -> k [(gacc, Nop)] (Some []) gacc

    let guardedSequentialProduct terms =
        let simplify = genericSimplify >> function
            | [] -> None
            | [(True, v)] -> Some v
            | ts -> Some (Union Metadata.empty ts)
        guardedSequentialProductRec True terms (fun errors results computationExistsGuard ->
            let results =
                match results with
                | Some results ->
                    Cps.List.mapk (fun r k -> Option.bind k (simplify r)) results Some
                | None -> None
            let errors = simplify errors |> Option.filter ((<>) Nop)
            errors, Option.map (withFst computationExistsGuard) results)
