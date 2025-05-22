# Discord-Sky
## Summary
This is a silly discord bot that will listen for a bot command, take in context from the chat, generate a ChatGPT prompt based on: (a static PREFIX, a chat-specified middle section, and a static SUFFIX) then respond as a chat message in that with the completion from ChatGPT.

## How to use
Please refer to `env_template.sh` to create your own `env.sh` file by copying `env_template.sh` to `env.sh` and then modify it fill in defaults and suit your needs. You will need a ChatGPT API account (which costs money per token). You will also need to create a Discord token for a bot.

### Here's the general idea behind the bot:
When a user specifies the bot prefix (which is the default `!react(USER SPECIFIED MIDDLE SECTION FOO)`), this bot will construct the string CHATGPT_PROMPT_PREFIX + "USER SPECIFIED MIDDLE SECTION FOO" + CHATGPT_PROMPT_SUFFIX, then submit it to ChatGPT/OpenAI to generate a "completion" string. That completion string will then be used as a new message that the bot will send to the chat channel it received the `!react` chat message in.

### Running the bot
- Run `python3 run_sky.py` to run the bot locally.
- The latest version of this bot has already been built into a docker image and is available for use at https://hub.docker.com/repository/docker/johnkordich/sky/general
    - You can run `./start_docker.sh` to pull and run the pre-built image.
    - You can run `./stop_docker.sh` to stop/remove the container from the step above
- You can run this in Kubernetes, check the "k8s" directory. Please replace the secrets in `discord-sky-secret-template.yaml` similarly to `env_template.sh` above.

## Development

### Testing
Discord-Sky uses pytest for unit testing. To run the tests:

```
pip install -r requirements-dev.txt
pytest tests/ -v
```

To run tests with coverage:

```
pytest tests/ -v --cov=sky
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
