// SPDX-FileCopyrightText: 2022 Kara
// SPDX-FileCopyrightText: 2022 Marat Gadzhiev
// SPDX-FileCopyrightText: 2022 Pieter-Jan Briers
// SPDX-FileCopyrightText: 2023 Cheackraze
// SPDX-FileCopyrightText: 2023 Eoin Mcloughlin
// SPDX-FileCopyrightText: 2023 Jezithyr
// SPDX-FileCopyrightText: 2023 Leon Friedrich
// SPDX-FileCopyrightText: 2023 TemporalOroboros
// SPDX-FileCopyrightText: 2023 Tom Leys
// SPDX-FileCopyrightText: 2023 Visne
// SPDX-FileCopyrightText: 2023 deltanedas
// SPDX-FileCopyrightText: 2023 deltanedas <@deltanedas:kde.org>
// SPDX-FileCopyrightText: 2023 eoineoineoin
// SPDX-FileCopyrightText: 2023 sTiKyt
// SPDX-FileCopyrightText: 2024 Checkraze
// SPDX-FileCopyrightText: 2024 Flesh
// SPDX-FileCopyrightText: 2024 GreaseMonk
// SPDX-FileCopyrightText: 2024 Nemanja
// SPDX-FileCopyrightText: 2024 Winkarst
// SPDX-FileCopyrightText: 2024 blueDev2
// SPDX-FileCopyrightText: 2024 checkraze
// SPDX-FileCopyrightText: 2024 icekot8
// SPDX-FileCopyrightText: 2024 metalgearsloth
// SPDX-FileCopyrightText: 2024 wafehling
// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Dvir
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 Whatstone
// SPDX-FileCopyrightText: 2025 sleepyyapril
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Cargo.Components;
using Content.Shared.Stacks;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.GameTicking;
using Robust.Shared.Map;
using Robust.Shared.Audio;
using Content.Shared.Whitelist; // Frontier
using Content.Server._NF.Cargo.Components; // Frontier
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Shared.Mobs;
using Robust.Shared.Containers; // Frontier
using Content.Shared._Mono.ItemTax.Components; // Mono
using Content.Server._NF.Bank;
using Content.Server._NF.Trade; // Mono
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Trade;
using Robust.Shared.Toolshed.Commands.Math; // Mono


namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    [Dependency] BankSystem _bank = default!; // Mono

    /*
     * Handles cargo shuttle / trade mechanics.
     */

    [Dependency] EntityWhitelistSystem _whitelist = default!; // Frontier

    // Frontier addition:
    // The maximum distance from the console to look for pallets.
    private const int DefaultPalletDistance = 8;

    private static readonly SoundPathSpecifier ApproveSound = new("/Audio/Effects/Cargo/ping.ogg");

    private void InitializeShuttle()
    {
        SubscribeLocalEvent<TradeStationComponent, GridSplitEvent>(OnTradeSplit);

        SubscribeLocalEvent<CargoShuttleConsoleComponent, ComponentStartup>(OnCargoShuttleConsoleStartup);

        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletSellMessage>(OnPalletSale);
        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletAppraiseMessage>(OnPalletAppraise);
        SubscribeLocalEvent<CargoPalletConsoleComponent, BoundUIOpenedEvent>(OnPalletUIOpen);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    #region Console

    private void UpdateCargoShuttleConsoles(EntityUid shuttleUid, CargoShuttleComponent _)
    {
        // Update pilot consoles that are already open.
        _console.RefreshDroneConsoles();

        // Update order consoles.
        var shuttleConsoleQuery = AllEntityQuery<CargoShuttleConsoleComponent>();

        while (shuttleConsoleQuery.MoveNext(out var uid, out var _))
        {
            var stationUid = _station.GetOwningStation(uid);
            if (stationUid != shuttleUid)
                continue;

            UpdateShuttleState(uid, stationUid);
        }
    }

    private void UpdatePalletConsoleInterface(Entity<CargoPalletConsoleComponent> uid) // Frontier: EntityUid<Entity
    {
        if (Transform(uid).GridUid is not { Valid: true } gridUid)
        {
            _uiSystem.SetUiState(uid.Owner,
                CargoPalletConsoleUiKey.Sale, // Frontier: uid<uid.Owner
                new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        // Frontier: per-object market modification
        GetPalletGoods(uid, gridUid, out var toSell, out var amount, out var noModAmount, out var blackMarketTaxAmount, out var frontierTaxAmount, out var nfsdTaxAmount, out var medicalTaxAmount);

        amount += noModAmount;
        // End Frontier

        // Monolith: display multiplier
        var station = _station.GetOwningStation(uid);
        var tradeCrateMultiplier = 1D;
        var otherMultiplier = 1D;

        if (TryComp<TradeCrateWildcardDestinationComponent>(station, out var wildcard))
            tradeCrateMultiplier = wildcard.ValueMultiplier;

        if (TryComp<MarketModifierComponent>(uid, out var marketModifier) && !marketModifier.Buy)
            otherMultiplier = marketModifier.Mod;

        _uiSystem.SetUiState(uid.Owner,
            CargoPalletConsoleUiKey.Sale, // Frontier: uid<uid.Owner
            new CargoPalletConsoleInterfaceState((int)amount, toSell.Count, true, tradeCrateMultiplier, otherMultiplier));
        // End Monolith
    }

    private void OnPalletUIOpen(EntityUid uid, CargoPalletConsoleComponent component, BoundUIOpenedEvent args)
    {
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    /// <summary>
    /// Ok so this is just the same thing as opening the UI, its a refresh button.
    /// I know this would probably feel better if it were like predicted and dynamic as pallet contents change
    /// However.
    /// I dont want it to explode if cargo uses a conveyor to move 8000 pineapple slices or whatever, they are
    /// known for their entity spam i wouldnt put it past them
    /// </summary>

    private void OnPalletAppraise(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletAppraiseMessage args)
    {
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    private void OnCargoShuttleConsoleStartup(EntityUid uid, CargoShuttleConsoleComponent component, ComponentStartup args)
    {
        var station = _station.GetOwningStation(uid);
        UpdateShuttleState(uid, station);
    }

    private void UpdateShuttleState(EntityUid uid, EntityUid? station = null)
    {
        TryComp<StationCargoOrderDatabaseComponent>(station, out var orderDatabase);
        TryComp<CargoShuttleComponent>(orderDatabase?.Shuttle, out var shuttle);

        var orders = GetProjectedOrders(uid, station ?? EntityUid.Invalid, orderDatabase, shuttle);
        var shuttleName = orderDatabase?.Shuttle != null ? MetaData(orderDatabase.Shuttle.Value).EntityName : string.Empty;

        if (_uiSystem.HasUi(uid, CargoConsoleUiKey.Shuttle))
            _uiSystem.SetUiState(uid, CargoConsoleUiKey.Shuttle, new CargoShuttleConsoleBoundUserInterfaceState(
                station != null ? MetaData(station.Value).EntityName : Loc.GetString("cargo-shuttle-console-station-unknown"),
                string.IsNullOrEmpty(shuttleName) ? Loc.GetString("cargo-shuttle-console-shuttle-not-found") : shuttleName,
                orders
            ));
    }

    #endregion

    private void OnTradeSplit(EntityUid uid, TradeStationComponent component, ref GridSplitEvent args)
    {
        // If the trade station gets bombed it's still a trade station.
        foreach (var gridUid in args.NewGrids)
        {
            EnsureComp<TradeStationComponent>(gridUid);
        }
    }

    #region Shuttle

    /// <summary>
    /// Returns the orders that can fit on the cargo shuttle.
    /// </summary>
    private List<CargoOrderData> GetProjectedOrders(
        EntityUid consoleUid,
        EntityUid shuttleUid,
        StationCargoOrderDatabaseComponent? component = null,
        CargoShuttleComponent? shuttle = null)
    {
        var orders = new List<CargoOrderData>();

        if (component == null || shuttle == null || component.Orders.Count == 0)
            return orders;

        var spaceRemaining = GetCargoSpace(consoleUid, shuttleUid);
        for (var i = 0; i < component.Orders.Count && spaceRemaining > 0; i++)
        {
            var order = component.Orders[i];
            if (order.Approved)
            {
                var numToShip = order.OrderQuantity - order.NumDispatched;
                if (numToShip > spaceRemaining)
                {
                    // We won't be able to fit the whole order on, so make one
                    // which represents the space we do have left:
                    var reducedOrder = new CargoOrderData(order.OrderId,
                            order.ProductId, order.ProductName, order.Price, spaceRemaining, order.Requester, order.Reason, null);
                    orders.Add(reducedOrder);
                }
                else
                {
                    orders.Add(order);
                }
                spaceRemaining -= numToShip;
            }
        }

        return orders;
    }

    /// <summary>
    /// Get the amount of space the cargo shuttle can fit for orders.
    /// </summary>
    private int GetCargoSpace(EntityUid consoleUid, EntityUid gridUid)
    {
        var space = GetCargoPallets(consoleUid, gridUid, BuySellType.Buy).Count;
        return space;
    }

    /// <summary>
    /// Frontier addition - calculates distance between two EntityCoordinates
    /// Used to check for cargo pallets around the console instead of on the grid.
    /// </summary>
    /// <param name="point1">first point to get distance between</param>
    /// <param name="point2">second point to get distance between</param>
    /// <returns></returns>
    public static double CalculateDistance(EntityCoordinates point1, EntityCoordinates point2)
    {
        var xDifference = point2.X - point1.X;
        var yDifference = point2.Y - point1.Y;

        return Math.Sqrt(xDifference * xDifference + yDifference * yDifference);
    }

    /// GetCargoPallets(gridUid, BuySellType.Sell) to return only Sell pads
    /// GetCargoPallets(gridUid, BuySellType.Buy) to return only Buy pads
    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent PalletXform)> GetCargoPallets(EntityUid consoleUid, EntityUid gridUid, BuySellType requestType = BuySellType.All)
    {
        _pads.Clear();

        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var compXform))
        {
            // Frontier addition - To support multiple cargo selling stations we add a distance check for the pallets.
            var distance = CalculateDistance(compXform.Coordinates, Transform(consoleUid).Coordinates);
            var maxPalletDistance = DefaultPalletDistance;

            // Get the mapped checking distance from the console
            if (TryComp<CargoPalletConsoleComponent>(consoleUid, out var cargoShuttleComponent))
            {
                maxPalletDistance = cargoShuttleComponent.PalletDistance;
            }

            var isTooFarAway = distance > maxPalletDistance;
            // End of Frontier addition

            if (compXform.ParentUid != gridUid ||
                !compXform.Anchored || isTooFarAway)
            {
                continue;
            }

            if ((requestType & comp.PalletType) == 0)
            {
                continue;
            }

            _pads.Add((uid, comp, compXform));

        }

        return _pads;
    }

    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)>
        GetFreeCargoPallets(EntityUid gridUid,
            List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)> pallets)
    {
        _setEnts.Clear();

        List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)> outList = new();

        foreach (var pallet in pallets)
        {
            var aabb = _lookup.GetAABBNoContainer(pallet.Entity, pallet.Transform.LocalPosition, pallet.Transform.LocalRotation);

            if (_lookup.AnyLocalEntitiesIntersecting(gridUid, aabb, LookupFlags.Dynamic))
                continue;

            outList.Add(pallet);
        }

        return outList;
    }

    #endregion

    #region Station

    private bool SellPallets(Entity<CargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out double amount, out double noMultiplierAmount, out double blackMarketTaxAmount, out double frontierTaxAmount, out double nfsdTaxAmount, out double medicalTaxAmount) // Frontier: first arg to Entity, add noMultiplierAmount
    {
        GetPalletGoods(consoleUid, gridUid, out var toSell, out amount, out noMultiplierAmount, out blackMarketTaxAmount, out frontierTaxAmount, out nfsdTaxAmount, out medicalTaxAmount); // Frontier: add noMultiplierAmount

        Log.Debug($"Cargo sold {toSell.Count} entities for {amount} (plus {noMultiplierAmount} without mods). (Taxes: Black Market: {blackMarketTaxAmount}, CO: {frontierTaxAmount}, TSFMC: {nfsdTaxAmount}, MD: {medicalTaxAmount})"); // Frontier: add section in parentheses

        if (toSell.Count == 0)
            return false;

        var ev = new EntitySoldEvent(toSell, gridUid); // Frontier: add gridUid
        RaiseLocalEvent(ref ev);

        // Collect all container entities and their contained entities recursively
        var allEntsToDelete = new HashSet<EntityUid>(toSell);

        // Make sure we delete all contained entities as well
        foreach (var ent in toSell)
        {
            if (TryComp<ContainerManagerComponent>(ent, out var containerManager))
            {
                // Recursively gather all entities inside containers
                var containedEntities = new HashSet<EntityUid>();
                GatherContainedEntities(ent, containerManager, containedEntities);
                allEntsToDelete.UnionWith(containedEntities);
            }
        }

        foreach (var ent in allEntsToDelete)
        {
            Del(ent);
        }

        return true;
    }

    /// <summary>
    /// Recursively gathers all entities inside containers
    /// </summary>
    private void GatherContainedEntities(EntityUid uid, ContainerManagerComponent containerManager, HashSet<EntityUid> containedEntities)
    {
        foreach (var container in containerManager.Containers.Values)
        {
            foreach (var entity in container.ContainedEntities)
            {
                containedEntities.Add(entity);

                // Recursively check containers inside this entity
                if (TryComp<ContainerManagerComponent>(entity, out var nestedContainers))
                {
                    GatherContainedEntities(entity, nestedContainers, containedEntities);
                }
            }
        }
    }

    private void GetPalletGoods(Entity<CargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out HashSet<EntityUid> toSell, out double amount, out double noMultiplierAmount, out double blackMarketTaxAmount, out double frontierTaxAmount, out double nfsdTaxAmount, out double medicalTaxAmount) // Frontier: first arg to Entity, add noMultiplierAmount
    {
        amount = 0;
        noMultiplierAmount = 0;
        blackMarketTaxAmount = 0;
        frontierTaxAmount = 0;
        nfsdTaxAmount = 0;
        medicalTaxAmount = 0;
        toSell = new HashSet<EntityUid>();

        foreach (var (palletUid, _, _) in GetCargoPallets(consoleUid, gridUid, BuySellType.Sell))
        {
            // Containers should already get the sell price of their children so can skip those.
            _setEnts.Clear();

            _lookup.GetEntitiesIntersecting(palletUid, _setEnts,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            foreach (var ent in _setEnts)
            {
                // Dont sell:
                // - anything already being sold
                // - anything anchored (e.g. light fixtures)
                // - anything blacklisted (e.g. players).
                if (toSell.Contains(ent) ||
                    _xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                {
                    continue;
                }

                // Frontier: whitelisted consoles
                if (_whitelist.IsWhitelistFail(consoleUid.Comp.Whitelist, ent))
                    continue;
                // End Frontier

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                // Mono: Use vending machine discount pricing for cargo sales
                var price = _pricing.GetPriceWithVendingDiscount(ent, gridUid);
                if (price == 0)
                    continue;
                toSell.Add(ent);

                var station = _station.GetOwningStation(ent);
                double multiplier = 1;

                if (station != null
                    && !HasComp<TradeCrateWildcardDestinationComponent>(station)
                    && TryComp<MarketModifierComponent>(consoleUid, out var marketModifier)
                    && !HasComp<IgnoreMarketModifierComponent>(ent)
                    && !marketModifier.Buy
                    && !HasComp<TradeCrateComponent>(ent))
                {
                    multiplier = marketModifier.Mod;
                }

                if (station != null
                    && TryComp<TradeCrateWildcardDestinationComponent>(station, out var wildcard)
                    && HasComp<TradeCrateComponent>(ent))
                {
                    multiplier = wildcard.ValueMultiplier;
                }

                // Frontier: check for items that are immune to market modifiers
                if (HasComp<IgnoreMarketModifierComponent>(ent))
                    noMultiplierAmount += price;
                else
                    amount += price * multiplier;


                // End Frontier: check for items that are immune to market modifiers
                // Mono: ItemTaxs to budgets.
                if (TryComp<ItemTaxComponent>(ent, out var itemTax))
                {
                    foreach (var (account, taxCoeff) in itemTax.TaxAccounts)
                    {
                        switch (account)
                        {
                            case SectorBankAccount.BlackMarket:
                                blackMarketTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.Frontier:
                                frontierTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.Nfsd:
                                nfsdTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.Medical:
                                medicalTaxAmount += price * taxCoeff;
                                break;
                            default:
                                break;
                        }
                    }
                }
                // End Mono
            }
        }
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        // Frontier: Look for blacklisted items and stop the selling of the container.
        if (_blacklistQuery.HasComponent(uid))
            return false;

        // Frontier: allow selling dead mobs
        if (_mobQuery.TryComp(uid, out var mob) && mob.CurrentState != MobState.Dead)
            return false;
        // End Frontier

        var complete = IsBountyComplete(uid, out var bountyEntities);

        // Recursively check for mobs at any point.
        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (complete && bountyEntities.Contains(child))
                continue;

            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }

        return true;
    }

    private void OnPalletSale(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletSellMessage args)
    {
        var xform = Transform(uid);

        if (xform.GridUid is not { Valid: true } gridUid)
        {
            _uiSystem.SetUiState(uid, CargoPalletConsoleUiKey.Sale,
            new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        if (!SellPallets((uid, component), gridUid, out var price, out var noMultiplierPrice, out var blackMarketTaxAmount, out var frontierTaxAmount, out var nfsdTaxAmount, out var medicalTaxAmount)) // Frontier: convert first arg to Entity, add noMultiplierPrice
            return;

        price += noMultiplierPrice;

        // End Frontier: market modifiers & immune objects
        // Mono Begin
        if (blackMarketTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.BlackMarket, (int)blackMarketTaxAmount, LedgerEntryType.BlackMarketSales);
        if (frontierTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Frontier, (int)frontierTaxAmount, LedgerEntryType.ColonialOutpostSales);
        if (nfsdTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Nfsd, (int)nfsdTaxAmount, LedgerEntryType.TSFMCSales);
        if (medicalTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Medical, (int)medicalTaxAmount, LedgerEntryType.MedicalSales);
        if (blackMarketTaxAmount < 0)
        {
            blackMarketTaxAmount = -blackMarketTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.BlackMarket, (int)blackMarketTaxAmount, LedgerEntryType.BlackMarketPenalties);
        }
        if (frontierTaxAmount < 0)
        {
            frontierTaxAmount = -frontierTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.Frontier, (int)frontierTaxAmount, LedgerEntryType.ColonialOutpostPenalties);
        }
        if (nfsdTaxAmount < 0)
        {
            nfsdTaxAmount = -nfsdTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.Nfsd, (int)nfsdTaxAmount, LedgerEntryType.TSFMCPenalties);
        }
        if (medicalTaxAmount < 0)
        {
            medicalTaxAmount = -medicalTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.Medical, (int)medicalTaxAmount, LedgerEntryType.MedicalPenalties);
        }
        // Mono End
        var stackPrototype = _protoMan.Index<StackPrototype>(component.CashType);
        _stack.Spawn((int)price, stackPrototype, xform.Coordinates);
        _audio.PlayPvs(ApproveSound, uid);
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    #endregion

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        Reset();
        CleanupTradeCrateDestinations(); // Frontier
    }
}

/// <summary>
/// Event broadcast raised by-ref before it is sold and
/// deleted but after the price has been calculated.
/// </summary>
[ByRefEvent]
public readonly record struct EntitySoldEvent(HashSet<EntityUid> Sold, EntityUid Grid);
