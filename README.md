# Discord-Sky
## Summary
Discord Sky is a modular Discord agent built around a lightweight plugin system. Each plugin hooks into the shared lifecycle to compose richer behaviour without tangled event handlers. The default bundle ships with:

- **Chat plugin** – turns prefixed chat into OpenAI conversations using the channel history.
- **Image plugin** – watches for text + image combinations and calls OpenAI's image generation pipeline.
- **Notifier plugin** – optionally scrapes configured job boards and DMs curated summaries on a schedule.

## Architecture
The entrypoint (`discord_sky.main`) wires the shared HTTP session, creates a `BotAgent`, and registers the plugins it needs. Plugins extend a tiny base class with lifecycle hooks:

1. `on_loaded` – prepare resources when the agent starts.
2. `on_ready` – run once Discord confirms the connection.
3. `handle_message` – decide whether and how to respond to each message.
4. `on_shutdown` – release background tasks cleanly.

This separation keeps each capability independent while still sharing transport, configuration, and logging.

### Context pipeline

OpenAI calls are prepared through `ConversationContextBuilder`, which selects the relevant channel history and any registered tool outputs. The builder exposes:

- configurable history character budgets and message filters via `Settings`.
- `add_tool_provider` hooks for attaching domain-specific tool results (search, retrieval, etc.).
- `render_prompt()` on the resulting `ConversationContext` to gather the final string payload sent to the API.

This modular layer keeps the logic for “which messages and artefacts reach OpenAI” in one place, making future tool integrations straightforward.

## How to use
Please refer to `env_template.sh` to create your own `env.sh` file by copying `env_template.sh` to `env.sh` and then modify it fill in defaults and suit your needs. You will need a ChatGPT API account (which costs money per token). You will also need to create a Discord token for a bot.

### Here's the general idea behind the bot:
When a user specifies the bot prefix (which is the default `!react(USER SPECIFIED MIDDLE SECTION FOO)`), this bot will construct the string CHATGPT_PROMPT_PREFIX + "USER SPECIFIED MIDDLE SECTION FOO" + CHATGPT_PROMPT_SUFFIX, then submit it to ChatGPT/OpenAI to generate a "completion" string. That completion string will then be used as a new message that the bot will send to the chat channel it received the `!react` chat message in.

Additionally, when a user posts an image with a text description, the bot will use the image and text to generate a new image using OpenAI's DALL-E model and post it to the chat channel.

### Running the bot
- Run `./run_sky.sh` (after creating `env.sh`) to launch the bot locally. The script now executes `python -m discord_sky.main`, which wires the modular services together.
- The latest version of this bot has already been built into a docker image and is available for use at https://hub.docker.com/repository/docker/johnkordich/sky/general
    - You can run `./start_docker.sh` to pull and run the pre-built image. Additional environment variables (such as image settings and notifier URLs) are now passed through.
    - You can run `./stop_docker.sh` to stop/remove the container from the step above
- You can run this in Kubernetes, check the "k8s" directory. Please replace the secrets in `discord-sky-secret-template.yaml` similarly to `env_template.sh` above.

### Configuration overview
Configuration is powered by Pydantic. The required environment variables are unchanged from the legacy bot:

- `BOT_TOKEN`, `BOT_PREFIX`, `BOT_CHANNELS`, `BOT_CONTEXT`, `BOT_MESSAGE_LIMIT`
- `CHATGPT_API_KEY`, `CHATGPT_MODEL`, `CHATGPT_PROMPT_PREFIX`, `CHATGPT_PROMPT_SUFFIX`, `CHATGPT_USER_SPECIFIED_MIDDLE_SECTION`

Optional variables introduce richer behaviour:

- `OPENAI_IMAGE_MODEL`, `OPENAI_IMAGE_SIZE` – tune the image pipeline
- `DM_USER_ID`, `MINUTES_BETWEEN_MESSAGES`, `URL_TO_FETCH`, `URL_TO_FETCH2` – enable the job notifier loop when all are provided
- `HTTP_TIMEOUT_SECONDS` – customise outbound HTTP timeouts

## Development

### Testing
Discord-Sky uses pytest for unit testing. To run the tests:

```
pip install -r requirements-dev.txt
pytest tests/ -v
```

To run tests with coverage:

```
pytest tests/ -v --cov=discord_sky
```

### Linting
We use flake8 for code linting:

```
pip install -r requirements-dev.txt
flake8 sky.py
```

## CI/CD Pipeline

Discord-Sky uses GitHub Actions for continuous integration and delivery. The following workflows are available:

1. **CI** - Runs linting and tests on every push and pull request
2. **Docker Build** - Builds a Docker image on push to main branch or release tags
3. **Docker Publish** - Builds and publishes the Docker image to Docker Hub when a new tag is created
4. **Deploy** - Builds and pushes a Docker image, then deploys to a Kubernetes cluster when code is merged to the main branch

To publish a new version:
1. Create and push a new tag (e.g., `git tag v1.0.0 && git push origin v1.0.0`)
2. The Docker Publish workflow will automatically build and push the image to Docker Hub

Continuous Deployment:
- When code is merged to the main branch, it automatically triggers the deployment pipeline
- The latest code is built as a Docker image and pushed to Docker Hub
- The application is then deployed to the configured Kubernetes cluster

### Required Secrets for CI/CD
For the Docker Publish workflow to work, you need to set up the following secrets in your GitHub repository:
- `DOCKERHUB_USERNAME`: Your Docker Hub username
- `DOCKERHUB_TOKEN`: Your Docker Hub access token

For the Deploy workflow to work, you also need:
- `KUBE_CONFIG`: Base64-encoded Kubernetes configuration file (kubeconfig)
