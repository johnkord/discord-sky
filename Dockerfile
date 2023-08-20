FROM python:3.8-slim-buster
WORKDIR /app
COPY ./requirements.txt /app
RUN pip install --trusted-host pypi.python.org -r requirements.txt
RUN apt update && apt install -y expect-dev
COPY . /app
CMD ["unbuffer", "python3", "sky.py"]
