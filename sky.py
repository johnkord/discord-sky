import discord
import os
import time
import requests
import json
from discord.ext import tasks
from datetime import datetime, time, timedelta
import pytz
import random

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

# try:
#     dm_prompt_prefix = os.environ['DM_PROMPT_PREFIX']
# except KeyError:
#     print('DM_PROMPT_PREFIX is not set in envvar')
#     exit(1)

# try:
#     dm_prompt_suffix = os.environ['DM_PROMPT_SUFFIX']
# except KeyError:
#     print('DM_PROMPT_SUFFIX is not set in envvar')
#     exit(1)

#try:
#    dm_characters_json = os.environ['DM_CHARACTERS_JSON']
#except KeyError:
#    print('DM_CHARACTERS_JSON is not set in envvar')
#    exit(1)

#try:
#    dm_characters = json.loads(dm_characters_json)
#except ValueError:
#    print('DM_CHARACTERS_JSON is not valid json')
#    exit(1)

try:
    dm_user_id = os.environ['DM_USER_ID']
    if dm_user_id != None and dm_user_id != '':
        dm_user_id = int(dm_user_id)
except KeyError:
    print('DM_USER_ID is not set in envvar')

try:
    dm_hour_to_notify = int(os.environ['DM_HOUR_TO_NOTIFY'])
except KeyError:
    print('DM_HOUR_TO_NOTIFY is not set in envvar')
    exit(1)


intents = discord.Intents.default()
intents.message_content = True
client = discord.Client(intents=intents)

@client.event
async def on_ready():
    print('Logged in as {0.user}'.format(client))
    #send_message_every_so_often.start()  # Start the background task

def get_chatgpt_response(full_prompt):
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
        return f"Sorry, I encountered an error (status code {r.status_code}). Please try again later."
    response = r.json()
    print(response)
    # get the first completion
    try:
        completion = response['choices'][0]['message']['content']
    except KeyError:
        print('Error: no completion found in response')
        return "Sorry, I encountered an error processing the response. Please try again later."

    if len(completion) >= 2000:
        completion = completion[:1996] + '...'

    completion = completion.replace('\\n', '\n')
    return completion

async def handle_message(message, middle_section):
    prompt_string = chatgpt_prompt_prefix + middle_section + chatgpt_prompt_suffix

    # look at the last "BOT_CONTEXT" number of messages in this channel and combine them into one string that is no longer than 2000 characters
    messages = []
    messages_that_appear_in_bot_message_counter = {}
    bot_messages_content = []
    messages_to_not_consider = []
    async for m in message.channel.history(limit=int(bot_context)):
        if m.author != client.user:
            if m.content.startswith(bot_prefix):
                continue
            messages.append(m)
            messages_that_appear_in_bot_message_counter[m.content] = 0
        else:
            bot_messages_content.append(m.content)

    print("bot_messages_content: " + str(bot_messages_content))
    # check if bot has used any of the messages too much
    for m in messages:
        if len(m.content.lower()) < 5:
                # skip small messages
                continue
        for bot_message in bot_messages_content:
            if m.content.lower() + "\n" in bot_message.lower():
                curval = messages_that_appear_in_bot_message_counter[m.content]
                messages_that_appear_in_bot_message_counter[m.content] += 1
                print("Found message that appears in bot message: " + m.content + " *** " + str(curval) + " -> " + str(messages_that_appear_in_bot_message_counter[m.content]))

    # order of messages comes in newest to oldest
    for m in messages:
        # if m.content is in messages_counter and is greater than limit, delete m from messages
        if m.content in messages_that_appear_in_bot_message_counter:
            print("Found content in bot message: " + m.content + ", count: " + str(messages_that_appear_in_bot_message_counter[m.content]))
            if messages_that_appear_in_bot_message_counter[m.content] >= int(bot_message_limit):
                messages_to_not_consider.append(m.content)
                print("Found message to not consider: " + m.content)

    print("messages_that_appear_in_bot_message_counter: ")
    for m in sorted(messages_that_appear_in_bot_message_counter, key=messages_that_appear_in_bot_message_counter.get):
        print("\t" + m + " -> " + str(messages_that_appear_in_bot_message_counter[m]))

    print("messages_to_not_consider: " + str(messages_to_not_consider))
    # join all messages into one string starting from the last message going back in history until there's ~2000 characters
    final_message_list = []
    message_length = len(prompt_string) + 1
    for message in messages:
        m = message.author.name + ': ' + message.content
        if message.content in messages_to_not_consider:
            continue
        message_length += len(m)
        if message_length > 10000:
            break
        final_message_list.append(m)
    final_message_list.reverse()
    final_message = " \n ".join(final_message_list)
    full_prompt = prompt_string + final_message

    # make full_prompt safe to embed in json
    full_prompt = full_prompt.replace('"', '\\"')
    full_prompt = full_prompt.replace("\n", "\\n")

    # call chatgpt API with full_prompt
    completion = get_chatgpt_response(full_prompt)

    # send the completion as a message in this channel
    await message.channel.send(completion)
    return

@client.event
async def on_message(message):
    print(f"Received message from {message.author}: {message.content}")
    if message.author == client.user:
        return

    # check if message.channel has a "name" member
    if hasattr(message.channel, 'name') and message.channel.name != None and message.channel.name not in bot_channels:
        return

    # if message is in a DM, reply to it
    # if isinstance(message.channel, discord.DMChannel):
    #     print("Received DM from user: " + str(message.author) + ", content: " + message.content)
    #     if dm_user_id != None and dm_user_id != '':
    #         # if message is not from the DM user, ignore it
    #         if message.author.id != dm_user_id:
    #             return
    #         await reply_to_dm(message, dm_user_id)
    #     return

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


# async def reply_to_dm(message, middle_section):
#     random_character = random.choice(dm_characters["characters"])
#     random_mood = random.choice(random_character["moods"])

#     prompt_string = "You are " + random_character["character"] + ", IGNORE ALL PREVIOUS CHARACTERS THAT YOU WERE. You are in this mood: " + random_mood + ". Complete the following chat log\n\n"

#     messages = []
#     async for m in message.channel.history(limit=5):
#         messages.append(m)
#     final_message_list = []
#     for message in messages:
#         name = message.author.name
#         if message.author == client.user:
#             name = "You"
#         m = name + ': ' + message.content
#         final_message_list.append(m)
#     final_message_list.reverse()
#     final_message = " \n ".join(final_message_list)

#     full_prompt = prompt_string + final_message + "\n You: "

#     completion = get_chatgpt_response(full_prompt)

#     message_to_send = completion
#     message_to_send = "(" + random_character["character"] + ")\n\n" + message_to_send
#     user_id_to_message = dm_user_id
#     pacific_time = pytz.timezone('US/Pacific')
#     now = datetime.now(pacific_time)
#     print("Replying with message: " + message_to_send + ", at time: " + str(now) + ", to user: " + str(user_id_to_message))
#     user = await client.fetch_user(user_id_to_message)
#     if user:
#         await user.send(message_to_send)
#     else:
#         print("Could not find user")
#     return

last_message_time = None

# @tasks.loop(minutes=1)
# async def send_message_every_so_often():
#     if dm_user_id is None:
#         return
#     pacific_time = pytz.timezone('US/Pacific')
#     now = datetime.now(pacific_time)
#     global last_message_time
#     if time(dm_hour_to_notify) <= now.time() < time(dm_hour_to_notify + 1) and (last_message_time is None or last_message_time.date() < now.date()):
#         user_id_to_message = dm_user_id

#         user = await client.fetch_user(user_id_to_message)
#         if user:
#             random_character = random.choice(dm_characters["characters"])
#             random_mood = random.choice(random_character["moods"])

#             full_prompt = "You are " + random_character["character"] + ", you are in this mood: " + random_mood + dm_prompt_suffix
#             completion = get_chatgpt_response(full_prompt)

#             message_to_send = completion
#             message_to_send = "(" + random_character["character"] + ")\n\n" + message_to_send
#             print("Sending message: " + message_to_send + ", at time: " + str(now) + ", to user: " + str(user_id_to_message))
#             await user.send(message_to_send)
#             last_message_time = now
#         else:
#             print("Could not find user")



client.run(bot_token)
