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
