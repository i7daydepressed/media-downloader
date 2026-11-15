.DEFAULT_GOAL := start

COMPOSE := docker compose

.PHONY: start stop restart logs status build help

start:
	$(COMPOSE) up -d --build app

stop:
	$(COMPOSE) stop app

restart:
	$(COMPOSE) up -d --force-recreate --no-build --pull never app

logs:
	$(COMPOSE) logs --follow --tail=100 app

status:
	$(COMPOSE) ps

build:
	$(COMPOSE) build app

help:
	@printf '%s\n' \
		'make / make start — собрать и запустить приложение' \
		'make stop — остановить приложение' \
		'make restart — пересоздать приложение с текущим .env, без сборки' \
		'make logs — смотреть последние логи и новые записи' \
		'make status — показать контейнеры проекта' \
		'make build — собрать образ без запуска' \
		'make help — показать команды'
