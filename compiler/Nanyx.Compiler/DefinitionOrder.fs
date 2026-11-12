namespace NyxCompiler

open Parser.Program

/// The order the top-level value definitions of a module are inferred in. A definition is inferred after those it
/// uses, so that a use sees their types rather than a variable that only unifying later will fill in: a pipe on the
/// result of a function defined further down finds the function attached to its type (#421). Definitions that use
/// each other, directly or through others, are inferred in the order they are written, as before.
module DefinitionOrder =
    /// For each top-level value definition, by its index in `items`, the indices of the definitions it uses that are
    /// not in a cycle with it, in the order they are written. Shadowing is ignored, so a definition may wait on one
    /// it does not use, which only means inferring that one first.
    let inferredAfter (items: TopLevelItem list) : Map<int, int list> =
        let defs =
            items
            |> List.indexed
            |> List.choose (fun (index, item) ->
                match item.Kind with
                | Def (ValueDef(_, name, _, value)) -> Some (index, name, value)
                | _ -> None)
        let indexOfName =
            defs |> List.fold (fun acc (index, name, _) -> if Map.containsKey name acc then acc else Map.add name index acc) Map.empty
        let uses =
            defs
            |> List.map (fun (index, _, value) ->
                index,
                SyntaxTree.referencedNames value |> List.choose (fun name -> Map.tryFind name indexOfName) |> List.distinct |> List.sort)
            |> Map.ofList

        // Tarjan's algorithm: the strongly connected components of the use graph, each named by its root.
        let mutable nextIndex = 0
        let mutable visitIndex = Map.empty
        let mutable lowLink = Map.empty
        let mutable stack = []
        let mutable onStack = Set.empty
        let mutable componentOf = Map.empty
        let rec visit node =
            visitIndex <- visitIndex |> Map.add node nextIndex
            lowLink <- lowLink |> Map.add node nextIndex
            nextIndex <- nextIndex + 1
            stack <- node :: stack
            onStack <- onStack |> Set.add node
            for used in uses.[node] do
                if not (visitIndex.ContainsKey used) then
                    visit used
                    lowLink <- lowLink |> Map.add node (min lowLink.[node] lowLink.[used])
                elif onStack.Contains used then
                    lowLink <- lowLink |> Map.add node (min lowLink.[node] visitIndex.[used])
            if lowLink.[node] = visitIndex.[node] then
                let rec pop () =
                    match stack with
                    | top :: rest ->
                        stack <- rest
                        onStack <- onStack |> Set.remove top
                        componentOf <- componentOf |> Map.add top node
                        if top <> node then pop ()
                    | [] -> ()
                pop ()
        for index, _, _ in defs do
            if not (visitIndex.ContainsKey index) then visit index

        uses |> Map.map (fun node used -> used |> List.filter (fun other -> componentOf.[other] <> componentOf.[node]))
