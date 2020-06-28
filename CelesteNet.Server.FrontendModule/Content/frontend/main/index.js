//@ts-check
import { rd, rdom, rd$, RDOMListHelper } from "../js/rdom.js";
import { DateTime } from "../js/deps/luxon.js";

function li(value) {
	return el => rd$(el)`<li>${value}</li>`;
}

function fetchStatus() {
	const el = document.getElementById("status-list");
	fetch("/status").then(r => r.json()).then(status => {
		for (let dummy of el.querySelectorAll(".dummy"))
			dummy.remove();

		const list = new RDOMListHelper(el);

		const startupDate = DateTime.fromMillis(status.StartupTime).setLocale("en-GB");
		list.add("uptime", li(`Last server restart: ${startupDate.toFormat("yyyy-MM-dd HH:mm:ss")}`));
		list.add("playersTotal", li(`Players since restart: ${status.PlayerCounter}`));
		list.add("playersReg", li(`Registered: ${status.PlayerRegistered}`));
		list.add("playersBan", li(`Banned: ${status.Bans}`));
		list.add("players", li(`Currently online: ${status.PlayerRefs}`));

		list.end();
	});
}

function renderUser() {
	const el = document.getElementById("userpanel-list");
	fetch("/userinfo").then(r => r.json()).then(info => {
		for (let dummy of el.querySelectorAll(".dummy"))
			dummy.remove();

		const list = new RDOMListHelper(el);

		if (info.Error) {
			// list.add("error", li(info.Error));
			list.add("login", el => rd$(el)`
				<a id="toplink-discord" class="button" href="/discordauth">
					<div class="toplink-icon"></div>
					<div class="toplink-text">Login via Discord</div>
				</a>`);
			list.end();
			return;
		}

		list.add("logout", el => rd$(el)`<a class="button" href="/deauth">Logout</a>`);

		list.end();
	});
}

setInterval(fetchStatus, 10000);
fetchStatus();
renderUser();
