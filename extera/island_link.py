"""Связь с островом DynamicIsland — демонстрация двух дорог, которые HTTP-лицо моста открыло плагину.

    телефон → остров   POST /note   заметка на каждое сообщение, и снятие заметки через {"gone": true}
    остров → телефон   GET  /wait   остров держит соединение, пока не будет чем ответить

Каждая дорога требует ?k= — тот же ключ, что остров печатает под своим QR-кодом. В самом хуке сети
нет: хук только кладёт заметку в очередь, а ждут и отправляют два потока. Ответы в чат по умолчанию
закрыты: плагин, который сам отвечает, хозяин должен сначала проверить.

Метаданные держятся простыми константами на верхнем уровне — загрузчик читает их из дерева, а не
из исполнения.
"""

import json
import queue
import threading
import urllib.parse
import urllib.request
from typing import Any, List

from base_plugin import BasePlugin, HookResult  # pyright: ignore[reportMissingImports]
from ui.settings import (  # pyright: ignore[reportMissingImports]
    Divider,
    Header,
    Input,
    Switch,
    Text,
)

__id__ = "island_link"
__name__ = "Остров"
__description__ = "Переносит сообщения на остров DynamicIsland и слушает, что остров ответит"
__author__ = "seli0n0"
__version__ = "1.0.0"
__icon__ = "exteraPlugins/1"
__app_version__ = ">=12.5.1"
__sdk_version__ = ">=1.4.4.3"

NEW_MESSAGES = "TL_updateNewMessage"
HOLD_SECONDS = 18  # остров держит /wait пятнадцать секунд и сдаёт пустое дело; socket сдаём чуть позже
PATIENCE = 6  # сколько ждём дорогу, которая обязана ответить сразу
BREATHE = 3  # пауза после неудачной дороги, чтобы не колотить в спящий компьютер
LONGEST = 700  # знаков заметки: остров показывает начало, а не весь разговор


class IslandLinkPlugin(BasePlugin):
    def on_plugin_load(self) -> None:
        self._notes: queue.Queue = queue.Queue()
        self._stop = threading.Event()
        self.add_hook(NEW_MESSAGES)
        self._start()
        self.log("остров: плагин загружен")

    def on_plugin_unload(self) -> None:
        self._stop.set()

    # --- дороги ---------------------------------------------------------------

    def _base(self) -> str:
        raw = str(self.get_setting("address", "") or "").strip()
        if not raw:
            return ""
        if not raw.startswith(("http://", "https://")):
            raw = "http://" + raw
        return raw.rstrip("/")

    def _road(self, road: str) -> str:
        key = str(self.get_setting("key", "") or "").strip()
        return f"{self._base()}{road}?k={urllib.parse.quote(key)}"

    def _ask(self, road: str, payload=None, timeout: int = PATIENCE):
        """Дорога острова: JSON пришёл — словарём, не пришёл — ничем.

        Спит ли компьютер, ошибся ли ключ, закрыта ли дверь — для демонстрации это одно и то же, и
        ни один из этих случаев не должен уронить поток.
        """
        if not self._base():
            return None
        headers = {"Connection": "close"}
        body = None
        if payload is not None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            self._road(road),
            data=body,
            headers=headers,
            method="POST" if body is not None else "GET",
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout) as answer:
                return json.loads(answer.read().decode("utf-8", "replace"))
        except Exception as ex:
            self.log(f"остров: {road} молчит ({ex})")
            return None

    # --- потоки ---------------------------------------------------------------

    def _start(self) -> None:
        if not self.get_setting("enabled", True):
            return
        self._stop.clear()
        threading.Thread(target=self._pusher, daemon=True, name="island-push").start()
        threading.Thread(target=self._puller, daemon=True, name="island-pull").start()

    def _pusher(self) -> None:
        while not self._stop.is_set():
            try:
                _, note = self._notes.get(timeout=1)
            except queue.Empty:
                continue
            self._ask("/note", note, timeout=PATIENCE)

    def _puller(self) -> None:
        while not self._stop.is_set():
            if not self._base():
                self._stop.wait(BREATHE)
                continue
            deed = self._ask("/wait", timeout=HOLD_SECONDS)
            if deed is None:
                self._stop.wait(BREATHE)
                continue
            kind = str(deed.get("do") or "")
            if not kind:
                continue  # пустое дело — это только «остров жив»; ждём снова
            self._deed(kind, str(deed.get("to") or ""), str(deed.get("text") or ""))

    # --- дела острова ---------------------------------------------------------

    def _deed(self, kind: str, to: str, text: str) -> None:
        if kind == "dismiss":
            # Урну нажали на острове: плитка снята там, на телефоне не меняется ничего.
            self._say(f"Остров убрал заметку {to}")
            return
        if kind == "answer":
            if not self.get_setting("answers", False):
                self._say("Остров хотел ответить — ответы закрыты в настройках плагина")
                return
            self._answer(to, text)
            return
        self.log(f"остров: дела {kind} плагин не знает")

    def _answer(self, to: str, text: str) -> None:
        """Ответ в тот же разговор, под каким номером заметка и пришла: знак и есть его id."""
        try:
            from client_utils import send_text  # pyright: ignore[reportMissingImports]

            send_text(int(to), text)
            self._say("Ответ с острова отправлен")
        except Exception as ex:
            self._say(f"Не удалось ответить: {ex}")

    def _say(self, text: str) -> None:
        self.log(text)
        if not self.get_setting("echo", True):
            return
        try:
            from android_utils import run_on_ui_thread  # pyright: ignore[reportMissingImports]
            from ui.bulletin import BulletinHelper  # pyright: ignore[reportMissingImports]

            run_on_ui_thread(lambda: BulletinHelper.show_info(text))
        except Exception as ex:
            self.log(f"остров: всплывающей надписи нет ({ex})")

    # --- входящие сообщения ---------------------------------------------------

    def on_update_hook(self, update_name: str, account: int, update: Any) -> HookResult:
        if update_name == NEW_MESSAGES and self.get_setting("incoming", True):
            self._bring(update, account)
        return HookResult()

    def _bring(self, update: Any, account: int) -> None:
        message = getattr(update, "message", None)
        if message is None:
            return
        dialog = _dialog(message)
        if dialog == 0:
            return
        if bool(getattr(message, "out", False)) and not self.get_setting("own", False):
            return
        said = str(getattr(message, "message", "") or "").strip() or _media(message)
        if not said:
            return
        sign = str(dialog)
        self._notes.put(
            (
                sign,
                {
                    "id": sign,
                    "app": "Экстераграмм",
                    "phone": _device(),
                    "title": _title(account, message),
                    "text": said[:LONGEST],
                    "clearable": True,
                    "silent": bool(self.get_setting("silent", False)),
                    "time": int(getattr(message, "date", 0) or 0) * 1000,
                },
            )
        )

    # --- ручная проверка ------------------------------------------------------

    def _probe(self, view: Any = None) -> None:
        answer = self._ask("/ping")
        if answer and answer.get("ok"):
            self._say(f"Остров отвечает: {answer.get('name', '')}")
        else:
            self._say("Остров молчит — проверьте адрес и ключ")

    def _show_and_take(self, view: Any = None) -> None:
        """Заметка, которая приходит и уходит сама: дороги /note в обе стороны одним нажатием."""

        def errand():
            note = {
                "id": "demo",
                "app": "Экстераграмм",
                "phone": _device(),
                "title": "Проверка связи",
                "text": "Эта заметка пришла с телефона и сейчас уйдёт.",
                "clearable": True,
                "silent": False,
            }
            if self._ask("/note", note) is None:
                self._say("Остров не принял заметку")
                return
            self._stop.wait(2.5)
            self._ask("/note", {"id": "demo", "gone": True})

        threading.Thread(target=errand, daemon=True, name="island-demo").start()

    def _reload(self, view: Any = None) -> None:
        self._stop.set()
        self._start()

    # --- настройки ------------------------------------------------------------

    def create_settings(self) -> List[Any]:
        return [
            Header(text="Остров"),
            Switch(
                key="enabled",
                text="Связь с островом",
                default=True,
                subtext="Держать открытым /wait и носить заметки",
                on_change=self._reload,
            ),
            Input(
                key="address",
                text="Адрес острова",
                default="",
                subtext="http://192.168.1.20:8484 — его остров печатает рядом с QR-кодом",
            ),
            Input(
                key="key",
                text="Ключ",
                default="",
                subtext="Буквы из ?k= под тем же QR-кодом",
            ),
            Divider(text="Что уходит на остров"),
            Switch(
                key="incoming",
                text="Входящие сообщения",
                default=True,
                subtext="Каждое новое сообщение — заметкой на острове",
            ),
            Switch(
                key="own",
                text="И отправленные мной",
                default=False,
                subtext="Остров видит и то, что пишу я сам",
            ),
            Switch(
                key="silent",
                text="Тихо",
                default=False,
                subtext="Только плитка на странице, без пилюли",
            ),
            Divider(text="Что приходит с острова"),
            Switch(
                key="answers",
                text="Разрешить острову отвечать",
                default=False,
                subtext="Нажатая на острове кнопка ответа отправит слова в тот же разговор",
            ),
            Switch(
                key="echo",
                text="Показывать дела острова",
                default=True,
                subtext="Всплывающей надписью на телефоне",
            ),
            Divider(text="Проверка"),
            Text(
                text="Спросить, жив ли остров",
                subtext="GET /ping",
                on_click=self._probe,
            ),
            Text(
                text="Показать заметку и убрать её",
                subtext="POST /note, потом то же с gone:true",
                on_click=self._show_and_take,
            ),
        ]


def _dialog(message: Any) -> int:
    """Разговор, к которому сообщение относится — те же правила, что у MessageObject.getDialogId."""
    given = int(getattr(message, "dialog_id", 0) or 0)
    if given:
        return given
    peer = getattr(message, "peer_id", None)
    if peer is None:
        return 0
    chat = int(getattr(peer, "chat_id", 0) or 0)
    if chat:
        return -chat
    channel = int(getattr(peer, "channel_id", 0) or 0)
    if channel:
        return -channel
    user = int(getattr(peer, "user_id", 0) or 0)
    if not user:
        return 0
    author = int(getattr(getattr(message, "from_id", None), "user_id", 0) or 0)
    if author == 0 or bool(getattr(message, "out", False)):
        return user
    return author


def _title(account: int, message: Any) -> str:
    """Надпись заметки: имя разговора, а в группе — ещё и имя того, кто сказал."""
    peer = getattr(message, "peer_id", None)
    if peer is None:
        return ""
    try:
        from org.telegram.messenger import MessagesController  # pyright: ignore[reportMissingImports]

        controller = MessagesController.getInstance(account)
        for field in ("chat_id", "channel_id"):
            ident = int(getattr(peer, field, 0) or 0)
            if not ident:
                continue
            chat = controller.getChat(ident)
            room = str(getattr(chat, "title", "") or "") or "Группа"
            author = _name(controller, int(getattr(getattr(message, "from_id", None), "user_id", 0) or 0))
            return f"{author} · {room}" if author and not bool(getattr(message, "out", False)) else room
        author = int(getattr(getattr(message, "from_id", None), "user_id", 0) or 0)
        return _name(controller, author or int(getattr(peer, "user_id", 0) or 0)) or "Личное сообщение"
    except Exception:
        return ""


def _name(controller: Any, ident: int) -> str:
    if not ident:
        return ""
    user = controller.getUser(ident)
    names = " ".join(
        part for part in (
            str(getattr(user, "first_name", "") or ""),
            str(getattr(user, "last_name", "") or ""),
        ) if part
    )
    return names or str(getattr(user, "username", "") or "")


def _media(message: Any) -> str:
    return "(вложение)" if getattr(message, "media", None) else ""


def _device() -> str:
    try:
        from android.os import Build  # pyright: ignore[reportMissingImports]

        return str(Build.MODEL)
    except Exception:
        return "Телефон"
