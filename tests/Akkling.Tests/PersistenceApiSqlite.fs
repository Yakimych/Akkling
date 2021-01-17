//-----------------------------------------------------------------------
// <copyright file="FsApi.fs" company="Akka.NET Project">
//     Copyright (C) 2009-2015 Typesafe Inc. <http://www.typesafe.com>
//     Copyright (C) 2013-2015 Akka.NET project <https://github.com/akkadotnet/akka.net>
//     Copyright (C) 2013-2015 Bartosz Sypytkowski <gttps://github.com/Horusiath>
// </copyright>
//-----------------------------------------------------------------------
module Akkling.Tests.PersistenceApiSqlite

open Akkling
open Akkling.Persistence
open Akkling.TestKit
open System
open Xunit

let config = Configuration.parse """
        akka {
            stdout-loglevel = DEBUG
            loglevel = DEBUG
            persistence.at-least-once-delivery.redeliver-interval = 5s

            persistence.journal {
                plugin = "akka.persistence.journal.sqlite"
                sqlite {
                    class = "Akka.Persistence.Sqlite.Journal.SqliteJournal, Akka.Persistence.Sqlite"
                    connection-string = "Data Source=test.db;Mode=Memory;Cache=Shared;"
                    auto-initialize = on
                }
            }
        }
    """

type ActorEvent =
    StateUpdated of int

type ActorCommand =
    | UpdateState of int
    | Crash
    | GetState

type ActorMessage =
    | ActorCommand of ActorCommand
    | ActorEvent of ActorEvent

[<Fact>]
let ``Typed actor with int state is restored after crash``() = test config <| fun tck ->
    let typedActor = spawn tck "test-actor-typed" <| propsPersist (fun ctx ->
        let rec loop (state: int) = actor {
            let! msg = ctx.Receive()

            match msg with
            | ActorCommand cmd ->
                match cmd with
                | GetState ->
                    ctx.Sender() <! state
                    return! loop state
                | Crash -> failwith "Crashing..."
                | UpdateState newState ->
                    let messageToPersist = ActorEvent <| StateUpdated newState
                    return Persist messageToPersist
            | ActorEvent evt ->
                match evt with
                | StateUpdated newState ->
                    return! loop newState }
        loop 0)

    let newState = 99
    typedActor <! (ActorCommand <| UpdateState newState)
    typedActor <! (ActorCommand <| Crash)

    async {
        let! actualNewState = typedActor <? ActorCommand ActorCommand.GetState // Hangs forever, since the actor is stopped
        Assert.Equal(newState, actualNewState)
    } |> Async.RunSynchronously

[<Fact>]
let ``Untyped actor with int state is restored after crash``() = test config <| fun tck ->
    let untypedActor = spawn tck "test-actor-untyped" <| propsPersist (fun ctx ->
        let rec loop (state: int) = actor {
            let! (msg: obj) = ctx.Receive()

            match msg with
            | :? PersistentLifecycleEvent as e ->
                typed tck.TestActor <! e
                return! loop state
            | :? ActorMessage as actorMsg ->
                match actorMsg with
                | ActorCommand cmd ->
                    match cmd with
                    | GetState ->
                        ctx.Sender() <! state
                        return! loop state
                    | Crash -> failwith "Crashing..."
                    | UpdateState newState ->
                        let messageToPersist = ActorEvent <| StateUpdated newState
                        return Persist (messageToPersist :> obj)
                | ActorEvent evt ->
                    match evt with
                    | StateUpdated newState ->
                        return! loop newState
            | _ -> return Unhandled }
        loop 0)

    let newState = 99
    untypedActor <! (ActorCommand <| UpdateState newState :> obj)
    untypedActor <! (ActorCommand <| Crash :> obj)

    expectMsg tck ReplaySucceed |> ignore

    async {
        let! actualNewState = untypedActor <? (ActorCommand ActorCommand.GetState :> obj)
        Assert.Equal(newState, actualNewState) // Assert.Equal() Failure Expected: 99 Actual: 0
    } |> Async.RunSynchronously
