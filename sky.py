import discord
import os
import time
import requests
import json

# Uses these API docs: https://platform.openai.com/docs/api-reference/chat

try:
    bot_channels = os.environ['BOT_CHANNELS']
except KeyError:
    bot_channels = 'bot-test,chat'
bot_channels = bot_channels.split(',')
print('bot_channels: ' + str(bot_channels))

try:
    bot_context = os.environ['BOT_CONTEXT']
except KeyError:
    print('BOT_CONTEXT is not set in envvar')
    exit(1)

try:
    bot_lru_cache_size = os.environ['BOT_LRU_CACHE_SIZE']
except KeyError:
    bot_lru_cache_size = 128

try:
    bot_message_limit = os.environ['BOT_MESSAGE_LIMIT']
except KeyError:
    bot_message_limit = 2

try:
    bot_prefix = os.environ['BOT_PREFIX']
except KeyError:
    print('BOT_PREFIX is not set in envvar')
    exit(1)

try:
    bot_token = os.environ['BOT_TOKEN']
except KeyError:
    print('BOT_TOKEN is not set in envvar')
    exit(1)

try:
    chatgpt_user_specified_middle_section = os.environ['CHATGPT_USER_SPECIFIED_MIDDLE_SECTION']
except KeyError:
    print('CHATGPT_USER_SPECIFIED_MIDDLE_SECTION is not set in envvar')
    exit(1)

try:
    chatgpt_api_key = os.environ['CHATGPT_API_KEY']
except KeyError:
    print('CHATGPT_API_KEY is not set in envvar')
    exit(1)

try:
    chatgpt_model = os.environ['CHATGPT_MODEL']
except KeyError:
    print('CHATGPT_MODEL is not set in envvar')
    exit(1)

try:
    chatgpt_prompt_prefix = os.environ['CHATGPT_PROMPT_PREFIX']
except KeyError:
    print('CHATGPT_PROMPT_PREFIX is not set in envvar')
    exit(1)

try:
    chatgpt_prompt_suffix = os.environ['CHATGPT_PROMPT_SUFFIX']
except KeyError:
    print('CHATGPT_PROMPT_SUFFIX is not set in envvar')
    exit(1)


intents = discord.Intents.default()
intents.message_content = True
client = discord.Client(intents=intents)

@client.event
async def on_ready():
    print('Logged in as {0.user}'.format(client))

seen_messages = {}
seen_messages_LRU = []

def hasUsedMessageTooMuch(message):
    if message in seen_messages:
        return seen_messages[message] > bot_message_limit
    else:
        return False

# Call this when coming across a new message
def registerMessageToLRU(m):
    global seen_messages
    global seen_messages_LRU
    if m not in seen_messages:
        print("Registering new message in LRU: " + m)
        seen_messages[m] = 0
        seen_messages_LRU.append(m)
        while len(seen_messages_LRU) > bot_lru_cache_size:
            removed_message = seen_messages_LRU.pop(0)
            del seen_messages[removed_message]

# Call this when safely identifying a message in a bot message
def incrementMessageCount(m):
    global seen_messages
    global seen_messages_LRU
    if m not in seen_messages:
        seen_messages[m] = 1
        seen_messages_LRU.append(m)
        while len(seen_messages_LRU) > bot_lru_cache_size:
            removed_message = seen_messages_LRU.pop(0)
            del seen_messages[removed_message]
    else:
        seen_messages[m] += 1
        # Bump message to the end of the LRU list
        seen_messages_LRU.remove(m)
        seen_messages_LRU.append(m)


async def handle_message(message, middle_section):
    global seen_messages
    global seen_messages_LRU
    print("=====================================")
    print(seen_messages)
    print(seen_messages_LRU)
    prompt_string = chatgpt_prompt_prefix + middle_section + chatgpt_prompt_suffix

    # look at the last "BOT_CONTEXT" number of messages in this channel and combine them into one string that is no longer than 2000 characters
    messages = []
    async for m in message.channel.history(limit=int(bot_context)):
        if m.author != client.user:
            if m.content.startswith(bot_prefix):
                continue
            registerMessageToLRU(m.content)
            if hasUsedMessageTooMuch(m.content):
                continue
            messages.append(m)
    # order of messages comes in newest to oldest

    # for each message, format it as "username: message"
    messages = [m.author.name + ': ' + m.content for m in messages]
    # join all messages into one string starting from the last message going back in history until there's ~2000 characters
    final_message_list = []
    message_length = len(prompt_string) + 1
    for m in messages:
        message_length += len(m)
        if message_length > 2000:
            break
        final_message_list.append(m)
    final_message_list.reverse()
    final_message = " \n ".join(final_message_list)
    full_prompt = prompt_string + final_message

    # make full_prompt safe to embed in json
    full_prompt = full_prompt.replace('"', '\\"')
    full_prompt = full_prompt.replace("\n", "\\n")

    # call chatgpt API with full_prompt

    url = 'https://api.openai.com/v1/chat/completions'
    headers = {'Authorization': 'Bearer ' + chatgpt_api_key}
    headers['Content-Type'] = 'application/json'
    data = {"model": chatgpt_model,"messages": [{"role": "user","content": full_prompt}]}

    print(data)
    r = requests.post(url, headers=headers, data=json.dumps(data))
    # check for errors
    if r.status_code != 200:
        print('Error: status code ' + str(r.status_code))
        print(r.text)
        return
    response = r.json()
    print(response)
    # get the first completion
    try:
        completion = response['choices'][0]['message']['content']
    except KeyError:
        print('Error: no completion found in response')
        return

    # For each message in seen_messages, check if contained in completion. If so, increment the count
    found_message = None
    for m in seen_messages_LRU:
        if len(m.lower()) < 5:
            # skip small messages
            continue
        if m.lower() + "\n" in completion.lower():
            found_message = m
            incrementMessageCount(m)
            break
    if found_message == None:
        print('NOT FOUND: message in completion')
    else:
        print('FOUND: message in completion: ' + m)

    if len(completion) >= 2000:
        completion = completion[:1996] + '...'

    completion = completion.replace('\\n', '\n')
    # send the completion as a message in this channel
    await message.channel.send(completion)
    return

@client.event
async def on_message(message):
    if message.author == client.user:
        return

    if message.channel.name not in bot_channels:
        return

    if message.content.startswith(bot_prefix + '('):
        try:
            middle_section = message.content.split('(', 1)[1].split(')', 1)[0]
        except IndexError:
            await message.channel.send('Error: could not parse middle_section specified in parentheses')
            return
        await handle_message(message, middle_section)
    elif message.content.startswith(bot_prefix):
        await handle_message(message, chatgpt_user_specified_middle_section)
        return

client.run(bot_token)
