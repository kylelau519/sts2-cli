"""In-process counterpart to env.py's Sts2Session, over the buffer protocol instead of JSON
stdin/stdout: no subprocess (RunSimulator runs directly in this Python process via pythonnet), and
no per-step dict/pydantic decode -- every call hands back a BufferDecision, a thin wrapper around a
numpy float32 array plus BufferSession-consistent offset math (see buffer_layout.py), not an object
graph. That object-graph decode step (JSON -> dict -> pydantic validate_python) was measured as the
actual bottleneck in whispering_earring's branch-worker/

Same public shape as Sts2Session (start/open_from_checkpoint/step/randomize_draw_pile/close,
action_log/character/seed/ascension) so callers built against one can switch to the other with
minimal churn -- the difference callers actually have to handle is what `step()`/`start()` return:
a BufferDecision (numpy-backed, buffer_layout accessors) instead of a pydantic Decision.
"""
from __future__ import annotations

import json
import queue
import threading

import numpy as np

import buffer_layout as bl
from actions import action
from clr_bridge import load_clr
from env import _DEFAULT_ENGINE_TIMEOUT, Sts2ActionError, Sts2EngineTimeoutError

_token_map: dict[int, str] | None = None  # int -> "CATEGORY:ID", loaded once per process


def _load_token_map() -> dict[int, str]:
    global _token_map
    if _token_map is None:
        raw = json.loads(load_clr().BufferSession.ExportTokenMap())
        _token_map = {int(k): v for k, v in raw.items()}
    return _token_map


def decode_token(token: int) -> str | None:
    """token -> the raw game entry id (e.g. 5 -> "STRIKE"), or None for UnknownToken (0) / an
    empty-slot sentinel (-1) / an unrecognized token. Builds on ExportTokenMap's "CATEGORY:ID"
    strings."""
    if token <= 0:
        return None
    key = _load_token_map().get(token)
    if key is None or key == "UNKNOWN":
        return None
    _, _, entry = key.partition(":")
    return entry


def _decode_card(slot: np.ndarray) -> dict | None:
    """slot is a raw CARD_SIZE block, or the first CARD_SIZE floats of a longer row (a hand/pile/
    shop/etc. slot) """
    if bl.card_token(slot) == 0:
        return None
    enchant_tok = int(slot[bl.CARD_ENCHANT_TOKEN])
    afflict_tok = int(slot[bl.CARD_AFFLICT_TOKEN])
    mask = int(slot[bl.CARD_KEYWORD_MASK])
    return {
        "id": decode_token(bl.card_token(slot)),
        "cost": float(slot[bl.CARD_COST]),
        "type": int(slot[bl.CARD_TYPE]),
        "rarity": int(slot[bl.CARD_RARITY]),
        "upgraded": bool(slot[bl.CARD_UPGRADED]),
        "upgradable": bool(slot[bl.CARD_UPGRADABLE]),
        "enchantment": decode_token(enchant_tok),
        "enchantment_amount": float(slot[bl.CARD_ENCHANT_AMOUNT]) if enchant_tok != -1 else None,
        "affliction": decode_token(afflict_tok),
        "affliction_amount": float(slot[bl.CARD_AFFLICT_AMOUNT]) if afflict_tok != -1 else None,
        "keywords": [name for name, bit in bl.CARD_KEYWORD_BITS.items() if mask & (1 << bit)],
    }


def _decode_hand_slot(slot: np.ndarray) -> dict | None:
    card = _decode_card(slot[:bl.CARD_SIZE])
    if card is None:
        return None
    card["can_play"] = bl.hand_can_play(slot)
    card["star_cost"] = bl.hand_star_cost(slot)
    card["target_type"] = bl.hand_target_type(slot)
    card["stats"] = {k: bl.hand_stat(slot, k) for k in bl.HAND_STAT_KEYS if bl.hand_stat(slot, k) != -1}
    card["damage_by_target"] = {}
    for i in range(bl.MAX_ENEMIES):
        dmg, repeat = bl.damage_by_target(slot, i)
        if dmg != -1:
            card["damage_by_target"][i] = {"damage": dmg, "repeat": repeat}
    return card


def _decode_enemy_slot(slot: np.ndarray) -> dict:
    intents = [
        {"type": itype, "damage": dmg}
        for i in range(bl.MAX_INTENTS_PER_ENEMY)
        for itype, dmg in [bl.enemy_intent(slot, i)] if itype != -1
    ]
    powers = [
        {"id": decode_token(int(pw[0])), "amount": float(pw[1])} for _, pw in bl.enemy_powers(slot)
    ]
    return {
        "monster": decode_token(bl.enemy_token(slot)),
        "hp": bl.enemy_hp(slot),
        "max_hp": bl.enemy_max_hp(slot),
        "block": bl.enemy_block(slot),
        "intends_attack": bl.enemy_intends_attack(slot),
        "intents": intents,
        "powers": powers,
    }


def _decode_player_summary(arr: np.ndarray) -> dict:
    return {
        "hp": bl.player_hp(arr),
        "max_hp": bl.player_max_hp(arr),
        "block": bl.player_block(arr),
        "gold": bl.player_gold(arr),
        "max_potions": bl.player_max_potions(arr),
        "deck_size": bl.player_deck_size(arr),
        "relics": [decode_token(int(slot[bl.RELIC_TOKEN])) for _, slot in bl.player_relics(arr)],
        "potions": [decode_token(int(slot[bl.POTION_TOKEN])) for _, slot in bl.player_potions(arr)],
        "deck": [_decode_card(slot) for _, slot in bl.deck_cards(arr)],
    }


def _decode_run_context(arr: np.ndarray) -> dict:
    raw = bl.run_context(arr)
    return {
        "act_index": raw["act_index"],
        "act_id": decode_token(raw["act_id_token"]),
        "floor": raw["floor"],
        "boss1": decode_token(raw["boss1_token"]),
        "boss2": decode_token(raw["boss2_token"]),
    }


def _decode_combat_play(arr: np.ndarray) -> dict:
    stars = bl.regent_stars(arr)
    osty = bl.osty(arr)
    alive_enemy_indices = [i for i, _ in bl.enemies(arr)]
    return {
        "round": bl.round_number(arr),
        "energy": bl.energy(arr),
        "max_energy": bl.max_energy(arr),
        "hand": [_decode_hand_slot(slot) for _, slot in bl.hand_cards(arr)],
        "enemies": {i: _decode_enemy_slot(slot) for i, slot in bl.enemies(arr)},
        "player_powers": [
            {"id": decode_token(int(slot[0])), "amount": float(slot[1])}
            for _, slot in bl.player_powers(arr)
        ],
        "draw_pile": {
            "count": bl.draw_pile_count(arr),
            "cards": [_decode_card(slot) for _, slot in bl.draw_pile_cards(arr)],
        },
        "discard_pile": {
            "count": bl.discard_pile_count(arr),
            "cards": [_decode_card(slot) for _, slot in bl.discard_pile_cards(arr)],
        },
        "exhaust_pile": {
            "count": bl.exhaust_pile_count(arr),
            "cards": [_decode_card(slot) for _, slot in bl.exhaust_pile_cards(arr)],
        },
        "orbs": [
            {"id": decode_token(int(slot[0])), "passive": float(slot[1]), "evoke": float(slot[2])}
            for _, slot in bl.orbs(arr)
        ],
        "orb_capacity": bl.orb_capacity(arr),
        "regent_stars": stars if stars != -1 else None,
        "osty": osty if osty["hp"] != -1 else None,
        "player_history": bl.player_history(arr),
        "card_generated_by_creator": bl.card_generated_by_creator(arr),
        "enemy_history": {i: bl.enemy_history(arr, i) for i in alive_enemy_indices},
    }


def _decode_map(arr: np.ndarray) -> dict:
    rows: dict[int, list[dict]] = {}
    boss = None
    for row, col, slot in bl.map_points(arr):
        point = {
            "type": bl.map_point_type(slot),
            "visited": bool(slot[1]),
            "current": bool(slot[2]),
            "children": list(bl.map_point_children(slot)),
        }
        if row == -1:
            boss = point
        else:
            point["col"] = col
            rows.setdefault(row, []).append(point)
    return {"rows": rows, "boss": boss}


def _decode_card_select(arr: np.ndarray) -> dict:
    return {
        "min_select": bl.min_select(arr),
        "max_select": bl.max_select(arr),
        "options": [_decode_card(slot) for _, slot in bl.select_cards(arr)],
    }


def _decode_shop(arr: np.ndarray) -> dict:
    cards = []
    for _, slot in bl.shop_cards(arr):
        card = _decode_card(slot[:bl.CARD_SIZE])
        assert card is not None  # shop_cards() already filters to occupied slots
        card["price"] = float(slot[bl.CARD_SIZE])
        card["is_stocked"] = bool(slot[bl.CARD_SIZE + 1])
        card["is_on_sale"] = bool(slot[bl.CARD_SIZE + 2])
        cards.append(card)
    return {
        "cards": cards,
        "relics": [
            {"id": decode_token(int(slot[0])), "price": float(slot[1]), "is_stocked": bool(slot[2])}
            for _, slot in bl.shop_relics(arr)
        ],
        "potions": [
            {"id": decode_token(int(slot[0])), "price": float(slot[1]), "is_stocked": bool(slot[2])}
            for _, slot in bl.shop_potions(arr)
        ],
        "removal_cost": bl.shop_removal_cost(arr),
    }


def _decode_rest_site(arr: np.ndarray) -> dict:
    return {
        "options": [
            {"index": i, "is_enabled": bool(slot[1])} for i, slot in bl.rest_options(arr)
        ]
    }


def _decode_card_reward(arr: np.ndarray) -> dict:
    return {
        "can_skip": bl.can_skip_reward(arr),
        "gold_delta": bl.reward_gold_delta(arr),
        "cards": [_decode_card(slot) for _, slot in bl.reward_cards(arr)],
    }


def _decode_event_choice(arr: np.ndarray) -> dict:
    return {
        "event": decode_token(bl.event_token(arr)),
        "options": [{"index": i, "is_locked": locked} for i, locked in bl.event_options(arr)],
    }


def _decode_bundle_select(arr: np.ndarray) -> dict:
    return {b: [_decode_card(slot) for _, slot in cards] for b, cards in bl.bundles(arr)}


_SECTION_DECODERS = {
    "combat_play": ("combat_play", _decode_combat_play),
    "map_select": ("map", _decode_map),
    "card_select": ("card_select", _decode_card_select),
    "shop": ("shop", _decode_shop),
    "rest_site": ("rest_site", _decode_rest_site),
    "card_reward": ("card_reward", _decode_card_reward),
    "event_choice": ("event_choice", _decode_event_choice),
    "event_card_reward": ("event_card_reward", lambda arr: {
        "cards": [_decode_card(slot) for _, slot in bl.event_card_reward_cards(arr)]
    }),
    "bundle_select": ("bundle_select", _decode_bundle_select),
    "game_over": ("game_over", lambda arr: {"victory": bl.victory(arr)}),
}


def decode_buffer(arr: np.ndarray) -> dict:
    """Full human-readable decode of one raw buffer record: every tokenized id resolved to its
    game entry string (via decode_token). Debugging/logging only """
    decision = bl.decision_name(arr)
    out: dict = {
        "decision": decision,
        "player": _decode_player_summary(arr),
        "run_context": _decode_run_context(arr),
    }
    entry = _SECTION_DECODERS.get(decision)
    if entry is not None:
        key, decoder = entry
        out[key] = decoder(arr)
    return out


class BufferDecision:
    """arr: a numpy float32 array this instance owns exclusively (a fresh copy out of
    BufferSession's reused native buffer -- see Sts2BufferSession._decision(); never aliases the
    C# side's buffer"""

    __slots__ = ("arr", "decision")

    def __init__(self, arr: np.ndarray):
        self.arr = arr
        self.decision = bl.decision_name(arr)

    @property
    def player_hp(self) -> int:
        return bl.player_hp(self.arr)

    @property
    def player_max_hp(self) -> int:
        return bl.player_max_hp(self.arr)

    @property
    def victory(self) -> bool:
        return bl.victory(self.arr) if self.decision == "game_over" else False

    def to_dict(self) -> dict:
        """Full human-readable decode -- see decode_buffer()'s docstring. For debugging/logging
        (printing a step, dumping to a log file, diffing two decisions) -- never call this from a
        training loop's hot path; that's exactly the per-step object-graph cost the buffer
        protocol exists to avoid."""
        return decode_buffer(self.arr)

    def __repr__(self) -> str:
        return f"BufferDecision({self.decision!r})"


class _ClrWorker:
    """One persistent daemon thread that serially runs blocking CLR calls handed to it via a
    request queue, replying via a per-call response queue -- Sts2BufferSession._call's actual
    timeout/recovery mechanism, split out so a session can swap in a fresh one after abandoning a
    stuck call (see _call's docstring) without repeating this bookkeeping. Amortizes thread-spawn
    cost across every call made through one worker (measured ~2.4x cheaper per call than spawning
    a fresh thread per call, since a hot branch-worker loop calls .Action() once per turn action)
    """

    def __init__(self) -> None:
        self._requests: queue.Queue = queue.Queue()
        self.thread = threading.Thread(target=self._loop, daemon=True, name="sts2-clr-call")
        self.thread.start()

    def _loop(self) -> None:
        while True:
            item = self._requests.get()
            if item is None:  # stop() sentinel
                return
            fn, args, response = item
            try:
                response.put(("ok", fn(*args)))
            except BaseException as e:  # noqa: BLE001 -- relayed to the caller's thread via
                # `response`, not swallowed; must catch broadly since fn is arbitrary CLR-call
                # surface (only Sts2ActionError/timeout are expected, but anything native-side
                # could in principle surface here).
                response.put(("error", e))

    def call(self, fn, *args, timeout: float):
        response: queue.Queue = queue.Queue(maxsize=1)
        self._requests.put((fn, args, response))
        kind, value = response.get(timeout=timeout)
        if kind == "error":
            raise value
        return value

    def stop(self) -> None:
        self._requests.put(None)

_abandoned_workers: list[threading.Thread] = []
_abandoned_lock = threading.Lock()


def _reap_abandoned_workers() -> None:
    """Raises Sts2EngineTimeoutError if any _ClrWorker thread ever abandoned (by a timeout) in
    this process is still running; otherwise clears out whichever ones have since finished and
    returns normally.  """
    with _abandoned_lock:
        pending = list(_abandoned_workers)
    still_running = [t for t in pending if t.is_alive()]
    if still_running:
        raise Sts2EngineTimeoutError(
            f"{len(still_running)} abandoned engine call(s) from an earlier timeout in this "
            "process are still running -- refusing to make a new native call while they could "
            "still race it (see _reap_abandoned_workers's docstring)"
        )
    with _abandoned_lock:
        for t in pending:
            if t in _abandoned_workers:
                _abandoned_workers.remove(t)


class Sts2BufferSession:
    def __init__(self, logger=None, timeout: float = _DEFAULT_ENGINE_TIMEOUT):
        _reap_abandoned_workers()
        self._session = load_clr().BufferSession()
        actual_size = int(self._session.BufferSize)
        assert actual_size == bl.TOTAL_SIZE, (
            f"buffer_layout.py ({bl.TOTAL_SIZE}) is out of sync with BufferSession.BufferSize "
            f"({actual_size}) -- RunSimulator.States.Buffer.cs's BufferLayout changed without "
            f"the Python mirror being updated to match"
        )
        self.logger = logger
        self.timeout = timeout
        self.action_log: list[dict] = []
        self.character: str | None = None
        self.seed: str | None = None
        self.ascension: int = 0
        self._worker = _ClrWorker()

    def _call(self, fn, *args):
        """Runs one blocking CLR call (RunSimulator, in-process via pythonnet) through this
        session's persistent _ClrWorker and bounds how long this method waits for it to return.
        """
        _reap_abandoned_workers()
        try:
            return self._worker.call(fn, *args, timeout=self.timeout)
        except queue.Empty:
            with _abandoned_lock:
                _abandoned_workers.append(self._worker.thread)
            self._worker = _ClrWorker()
            raise Sts2EngineTimeoutError(
                f"BufferSession call didn't respond within {self.timeout}s -- treating it (and "
                f"this whole process) as hung"
            ) from None

    def _decision(self, raw) -> BufferDecision:
        return BufferDecision(np.array(raw, dtype=np.float32))

    def start(self, character: str, seed: str, ascension: int = 0) -> BufferDecision:
        self.character, self.seed, self.ascension = character, seed, ascension
        self.action_log = []
        return self._decision(self._call(self._session.StartRun, character, seed, ascension))

    def open_from_checkpoint(self, character: str, seed: str, ascension: int,
                              actions: list[dict]) -> BufferDecision:
        """Same replay-to-reconstruct-state approach as Sts2Session.open_from_checkpoint """
        self.character, self.seed, self.ascension = character, seed, ascension
        raw = self._call(self._session.StartRun, character, seed, ascension)
        for entry in actions:
            args = entry.get("args")
            raw = self._call(self._session.Action, entry["action"], json.dumps(args) if args else None)
            if self._decision(raw).decision == "error":
                # See step()'s docstring -- a checkpoint's own prefix can itself contain an
                # action that only ever silently no-op'd (recorded as if it succeeded) rather
                # than genuinely happening; surface that here instead of quietly replaying a
                # divergent state for the rest of this branch.
                raise Sts2ActionError(self._session.LastError or "unknown error")
        self.action_log = list(actions)
        return self._decision(raw)

    def load_and_resume_combat(self, checkpoint_path: str, entry_action: dict,
                                actions: list[dict]) -> BufferDecision:
        """Faster alternative to open_from_checkpoint for reconstructing a state inside a
        combat"""
        self.load_save(checkpoint_path)
        combined = [entry_action, *actions]
        decision = None
        for entry in combined:
            args = entry.get("args")
            raw = self._call(self._session.Action, entry["action"], json.dumps(args) if args else None)
            decision = self._decision(raw)
            if decision.decision == "error":
                raise Sts2ActionError(self._session.LastError or "unknown error")
        self.action_log = combined
        return decision

    def step(self, action_name: str, **kwargs) -> BufferDecision:
        cmd = action(action_name, **kwargs)  # validates args the same way Sts2Session.step does
        self.action_log.append(cmd)
        if self.logger:
            self.logger.log_action(cmd)
        args = cmd.get("args")
        raw = self._call(self._session.Action, action_name, json.dumps(args) if args else None)
        decision = self._decision(raw)
        if decision.decision == "error":
            raise Sts2ActionError(self._session.LastError or "unknown error")
        if self.logger:
            self.logger.log_state(decision)
        return decision

    def randomize_draw_pile(self, state: BufferDecision, seed: int | None = None) -> None:
        if getattr(state, "decision", None) != "combat_play":
            return
        self._call(self._session.RandomizeDrawPile, seed if seed is not None else 0)

    def save(self, path: str) -> dict:
        """Write a native sts2 checkpoint at `path`."""
        result = json.loads(self._call(self._session.Save, path))
        if not result.get("success"):
            raise Sts2ActionError(result.get("message", "save failed"))
        return result

    def load_save(self, path: str) -> BufferDecision:
        """Load a native checkpoint written by save() and resume into it -- counterpart to
        save(), and the in-process equivalent of Sts2Session.load_save()."""
        self.action_log = []
        arr = self._call(self._session.LoadSave, path)
        decision = self._decision(arr)
        if decision.decision == "error":
            raise Sts2ActionError(self._session.LastError or "unknown error")
        return decision

    def close(self) -> None:
        self._worker.stop()
